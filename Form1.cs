using Microsoft.Win32;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using TwinCAT.Ads;   // 先以 NuGet 安裝 Beckhoff.TwinCAT.Ads
using CvRect = OpenCvSharp.Rect;   // 避免和 System.Drawing.Rectangle 搞混
using CvSize = OpenCvSharp.Size;   // 避免和 System.Drawing.Size 搞混
using OCPt = OpenCvSharp.Point;
using OCRect = OpenCvSharp.Rect;
using OCV = OpenCvSharp;           // 簡短別名

namespace DemoBox_Boiler
{

    public partial class FormMain : Form
    {
        // ===== timers =====
        private readonly Timer _fanTimer = new() { Interval = 80 };   // 鼓風機動畫
        private readonly Timer _uiTimer = new() { Interval = 200 };  // 週期重繪（儀表/溫度計等）

        // ===== fan frames（請先把 blower_000..blower_120 5 張加入 Resources）=====
        private Bitmap[] _fanFrames;
        private int _fanFrameIdx = 0;

        private Bitmap _gaugeMask;                // gauge 填充遮罩（gauge_fill_mask_140x110）
        private RectangleF _gaugeOuter, _gaugeInner; // 畫弧用的外/內框

        // ===== DI/DO 狀態（先用記憶體變數，之後綁 PLC）=====
        private bool diFlame = true, diDoor = true, diLowWater = false;

        // DO 規則：0=open、1=close（你指定的語意）
        private int doBlower = 1, doGasValve = 1, doCirc = 1, doFeed = 0;

        // ===== ADS 連線設定 =====
        private AdsClientEx _ads = null;
        private bool _adsConnected = false;
        private Timer _adsPoll = new Timer() { Interval = 200 }; // 200ms 輪詢

        // ★ Handle 全部用 uint（AdsClient 的 CreateVariableHandle 回傳 uint）
        private uint hTemp, hWaterLevel, hPress;
        private uint hDoBlower, hDoGas, hDoCirc, hDoFeed;
        private uint hDiFlame, hDiDoor, hDiLow;

        private const string PLC_PREFIX = "GVL.";

        // ======= 關閉控制 / 重新連線管理 =======
        private bool _isClosing = false;
        private readonly List<System.Windows.Forms.Timer> _retryTimers = new();

        // ===== Camera / Vision =====
        private VideoCapture _cap = null;
        private Timer _camTimer = new Timer() { Interval = 66 }; // 約 15 FPS
        private bool _camOn = false;
        private int _camIndex = 0; // 預設用本機第一支攝影機（改成 RTSP 也可）
        // ---- Fire detection state ----
        private OpenCvSharp.Mat _prevGray;   // 前一幀(灰階)供動態偵測
        private readonly Queue<bool> _fireHistory = new Queue<bool>();
        private const int FIRE_SMOOTH_N = 5;     // 最近 N 幀
        private const int FIRE_REQUIRE_K = 3;    // 至少 K 幀為 true 才算觸發
        // ---- Fire debouncing / hysteresis ----
        private readonly Queue<bool> _fireQ = new();  // 近期偵測結果佇列
        private bool _fireStable = false;             // 穩定狀態（送到 UI/ADS 用）
        private int _cooldown = 0;                   // 延遲關閉的計數器
        private OCRect _lastBox;                      // 上次穩定框（可當顯示/比對用）

        // 參數：依你的 FPS(≈15) 來估：ON 約 4~6 幀、OFF 約 12~18 幀
        private const int N_FRAMES = 6;   // 觀察窗長度
        private const int K_ON = 3;   // 最近 N 幀至少 K 幀為 true 才判定「開」
        private const int HOLD_OFF = 10;  // 失去偵測後，至少等這麼多幀才判定「關」

        // ===== ADS：火焰變數 =====
        private uint hFire = 0;                 // PLC 端 BOOL 變數 handle
        private bool _lastFireSent = false;     // 上次寫入狀態，降頻避免猛寫
        private const string FireSymbolPath = PLC_PREFIX + "bFireDetected";

        private void FormMain_Load(object sender, EventArgs e)
        {
            // ===== 靜態圖示 =====
            pbBoiler.Image = Properties.Resources.boiler_body;
            pbFlame.Image = Properties.Resources.flame;
            UpdateGasValveImage();
            pbCircPump.Image = Properties.Resources.pump;
            pbFeedPump.Image = Properties.Resources.pump;
            pbThermoShell.Image = Properties.Resources.thermometer_shell_48x324;

            // ===== DI 圖示（先用假資料）=====
            pbDI_Flame.Image = Properties.Resources.di_on;
            pbDI_Door.Image = Properties.Resources.di_on;
            pbDI_LowWater.Image = Properties.Resources.di_off;

            // ===== 鼓風機動畫影格（先在 Resources 加入 blower_000..270）=====
            _fanFrames = new Bitmap[]
            {
                Properties.Resources.blower_000, Properties.Resources.blower_030,
                Properties.Resources.blower_060, Properties.Resources.blower_090,
                Properties.Resources.blower_120, 
            };
            pbBlower.Image = _fanFrames[0];

            // 建議的幾何（以 140x110 畫面為基準，半圓在上半部）
            _gaugeMask = Properties.Resources.gauge_fill_mask_140x110;
            _gaugeOuter = new RectangleF(0, 0, 140, 140);                 // 外弧的圓框
            _gaugeInner = new RectangleF(24, 24, 140 - 48, 140 - 48);     // 內弧的圓框

            // ===== 綁定 DO「點擊切換」：0=open、1=close =====
            pbGasValve.Cursor = Cursors.Hand;
            pbGasValve.Click += (_, __) => ToggleValve(ref doGasValve, pbGasValve);

            pbCircPump.Cursor = Cursors.Hand;
            pbCircPump.Click += (_, __) => TogglePump(ref doCirc, pbCircPump, panelInfoCirc);

            pbFeedPump.Cursor = Cursors.Hand;
            pbFeedPump.Click += (_, __) => TogglePump(ref doFeed, pbFeedPump, panelInfoFeed);

            pbBlower.Cursor = Cursors.Hand;
            pbBlower.Click += (_, __) => ToggleBlower(ref doBlower);

            // ===== 啟動動畫/重繪 =====
            _fanTimer.Tick += (_, __) => {
                _fanFrameIdx = (_fanFrameIdx + 1) % _fanFrames.Length;
                pbBlower.Image = _fanFrames[_fanFrameIdx];
            };
            //_fanTimer.Start();
            UpdateFanAnimation();   // 依目前 doBlower 狀態決定是否轉動

            SyncDI();
            lblGaugeTempValue.BringToFront();
            lblGaugePressValue.BringToFront();

            lblAdsState.Click += lblConn_Click;
            UpdateConnUI(false);
            ClearAnalogUI();   // 開啟時先顯示空畫面，不要任何預設讀數

            // 讓 Camera 畫面可縮放填滿
            pbCameraView.SizeMode = PictureBoxSizeMode.StretchImage;
            // 點擊畫面 → 開/關相機
            pbCameraView.Cursor = Cursors.Hand;
            pbCameraView.Click += PbCameraView_Click;

            // Camera 計時器
            _camTimer.Tick += CamTimer_Tick;

            // Fire 告警預設樣式
            lblFire.Text = "Fire: off";
            lblFire.BackColor = Color.FromArgb(80, 40, 40, 40);
            lblFire.ForeColor = Color.White;
   
        }

        public FormMain()
        {
            InitializeComponent();
        }

        private void ToggleValve(ref int state, PictureBox pb)
        {
            state = 1 - state;  // 0↔1
            doGasValve = state; // 確保欄位同步（若外面已用 doGasValve 傳進來，這行可省略）

            UpdateGasValveImage();  // ← 統一更新圖示

            if (_adsConnected)
            {
                try { _ads.WriteAny(hDoGas, state == 1); } catch { }
            }
            UpdateInfoCards();
        }

        private void TogglePump(ref int state, PictureBox pb, Panel card)
        {
            state = 1 - state;
            if (_adsConnected)
            {
                try
                {
                    if (card == panelInfoCirc) _ads.WriteAny(hDoCirc, state == 1);
                    else if (card == panelInfoFeed) _ads.WriteAny(hDoFeed, state == 1);
                }
                catch { }
            }
            UpdateInfoCards();
        }

        private void ToggleBlower(ref int state)
        {
            state = 1 - state;  // 0↔1
            if (_adsConnected) { try { _ads.WriteAny(hDoBlower, state == 1); } catch { } }

            UpdateFanAnimation();   // ← 關鍵
            UpdateInfoCards();
        }


        private void UpdateInfoCards()
        {
            // 假設四張卡片裡有三個 Label（Name 依你設計器命名）
            // 下面是示意，請把對應的 Label 名稱換成你的：
            SetCard(panelInfoBlower, "Blower", doBlower);
            SetCard(panelInfoGas, "Gas Valve", doGasValve);
            SetCard(panelInfoCirc, "Circ Pump", doCirc);
            SetCard(panelInfoFeed, "Feed Pump", doFeed);
        }

        private void FormMain_FormClosing(object sender, FormClosingEventArgs e)
        {
            _isClosing = true;

            // 先停所有 UI/ADS 計時器
            try { _uiTimer.Stop(); } catch { }
            try { _fanTimer.Stop(); } catch { }
            try { _adsPoll.Stop(); } catch { }
            try { foreach (var t in _retryTimers) { t.Stop(); t.Dispose(); } _retryTimers.Clear(); } catch { }

            // 未連線狀態：直接 Dispose ADS Client，不做任何網路呼叫
            if (!_adsConnected)
            {
                try { _ads?.Dispose(); } catch { }
                _ads = null;
                return;
            }

            StopCamera();
            // 已連線狀態也要快：帶 fast=true，少做/不做網路 I/O
            AdsDisconnect(fast: true);
            ClearAnalogUI();   // 斷線時清空顯示
        }

        private void SetCard(Panel card, string title, int doState)
        {
            // 取出三行 Label（或用 Controls.Find 精準找）
            var labels = card.Controls.OfType<Label>().OrderBy(x => x.Top).ToArray();
            if (labels.Length < 3) return;

            labels[0].Text = title;
            labels[1].Text = $"DO: {(doState == 1 ? "close (1)" : "open (0)")}";
            labels[2].Text = $"V: 24.0 V   I: 0.60 A"; // 先放假資料；下一步綁定實測值
        }

        // 放在 FormMain 類別裡
        private void SyncDI()
        {
            // 圖示：on 用 di_on.png，off 用 di_off.png
            pbDI_Flame.Image = diFlame ? Properties.Resources.di_on : Properties.Resources.di_off;
            pbDI_Door.Image = diDoor ? Properties.Resources.di_on : Properties.Resources.di_off;
            pbDI_LowWater.Image = diLowWater ? Properties.Resources.di_on : Properties.Resources.di_off;

            // 旁邊那行文字（把控制項名稱改成你設計器上的實名）
            lbFlame.Text = "DO_Flame " + (diFlame ? "on" : "off");
            lbDoor.Text = "DO_Door " + (diDoor ? "on" : "off");
            lbLowwater.Text = "DO_LowWater " + (diLowWater ? "on" : "off");
        }

        // 快速 alpha 遮罩（建議放在 FormMain 內）
        private static void ApplyMaskAlpha(Bitmap src, Bitmap mask)
        {
            // 兩張圖的邊界保護
            int w = Math.Min(src.Width, mask.Width);
            int h = Math.Min(src.Height, mask.Height);

            // 逐像素套用遮罩的 R 當 alpha（白=255）
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    Color c = src.GetPixel(x, y);
                    byte a = mask.GetPixel(x, y).R;
                    src.SetPixel(x, y, Color.FromArgb((byte)(c.A * a / 255), c.R, c.G, c.B));
                }
            }
        }

        private void RenderGaugeEmpty(PictureBox target)
        {
            var track = Properties.Resources.gauge_track_140x110;
            var edge = Properties.Resources.gauge_overlay_edges_140x110;

            var bmp = new Bitmap(target.Width, target.Height);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(track, new Rectangle(0, 0, bmp.Width, bmp.Height));
                g.DrawImage(edge, new Rectangle(0, 0, bmp.Width, bmp.Height));
            }
            var old = target.Image; target.Image = bmp; old?.Dispose();
        }

        private void RenderThermometerEmpty(PictureBox target)
        {
            var shell = Properties.Resources.thermometer_shell_48x324;
            var bmp = new Bitmap(target.Width, target.Height);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(shell, new Rectangle(0, 0, bmp.Width, bmp.Height));
            }
            var old = target.Image; target.Image = bmp; old?.Dispose();
        }

        private void ClearAnalogUI()
        {
            RenderGaugeEmpty(pbGaugeTempOverlay);
            RenderGaugeEmpty(pbGaugePressOverlay);
            RenderThermometerEmpty(pbThermoShell);
            lblGaugeTempValue.Text = "--.- °C";
            lblGaugePressValue.Text = "--.- mV";
        }

        // 以「單一 PictureBox」畫出溫度計（直條 + 外殼）
        private void RenderThermometerTo(PictureBox target, double valueC, Color fillColor, float shellAlpha = 0.55f)
        {
            Bitmap mask = Properties.Resources.thermometer_fill_mask_48x324; // 白色遮罩
            Bitmap shell = Properties.Resources.thermometer_shell_48x324;     // 外殼(帶陰影)

            // 先在遮罩尺寸上做一張填色圖
            Bitmap fill = new Bitmap(mask.Width, mask.Height);
            using (Graphics g = Graphics.FromImage(fill))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

                // 0..120°C → 由下往上
                double r = Math.Max(0, Math.Min(1, valueC / 120.0));
                int yTop = (int)(12 + (286 - 12) * (1 - r));

                using (SolidBrush br = new SolidBrush(fillColor))
                    g.FillRectangle(br, 0, yTop, fill.Width, fill.Height - yTop);
            }

            // 用遮罩把填色裁出管腔形狀
            ApplyMaskAlpha(fill, mask);

            // 合成到目標大小
            Bitmap bmp = new Bitmap(target.Width, target.Height);
            using (Graphics g2 = Graphics.FromImage(bmp))
            {
                g2.Clear(Color.Transparent);
                g2.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;

                // 先畫填色
                g2.DrawImage(fill, new Rectangle(0, 0, bmp.Width, bmp.Height));

                // 再畫外殼，但把 alpha 降低（預設 0.55）
                DrawImageWithAlpha(g2, shell, new Rectangle(0, 0, bmp.Width, bmp.Height), shellAlpha);
            }

            Image old = target.Image;
            target.Image = bmp;
            if (old != null) old.Dispose();
            fill.Dispose();
        }

        // 將圖片以指定 alpha 繪製
        private static void DrawImageWithAlpha(Graphics g, Image img, Rectangle dest, float alpha)
        {
            using (var ia = new System.Drawing.Imaging.ImageAttributes())
            {
                var cm = new System.Drawing.Imaging.ColorMatrix();
                cm.Matrix33 = Math.Max(0f, Math.Min(1f, alpha)); // 只調整整體透明度
                ia.SetColorMatrix(cm, System.Drawing.Imaging.ColorMatrixFlag.Default,
                                     System.Drawing.Imaging.ColorAdjustType.Bitmap);
                g.DrawImage(img, dest, 0, 0, img.Width, img.Height, GraphicsUnit.Pixel, ia);
            }
        }

        // 以「單一 PictureBox」畫出半圓儀表（底軌 + 彩色弧 + 邊線）
        private void RenderGaugeTo(PictureBox target, double ratio01, Color fillColor)
        {
            // 來源資產（我提供的 PNG）
            Bitmap track = Properties.Resources.gauge_track_140x110;           // 底軌（灰）
            Bitmap mask = Properties.Resources.gauge_fill_mask_140x110;       // 填色遮罩（白）
            Bitmap edge = Properties.Resources.gauge_overlay_edges_140x110;   // 邊線（細灰，含透明）

            // 在遮罩尺寸上先畫彩色弧（200°..340°）
            float start = 200f;
            float sweep = (float)(140 * Math.Max(0, Math.Min(1, ratio01)));

            Bitmap fill = new Bitmap(mask.Width, mask.Height);
            using (Graphics g = Graphics.FromImage(fill))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

                // 外/內框（與資源對齊）
                RectangleF outer = new RectangleF(0, 0, 140, 220);
                RectangleF inner = new RectangleF(24, 24, 92, 172);

                // 畫外扇形
                using (System.Drawing.Drawing2D.GraphicsPath p = new System.Drawing.Drawing2D.GraphicsPath())
                {
                    p.AddPie(outer.X, outer.Y, outer.Width, outer.Height, start, sweep);
                    using (SolidBrush br = new SolidBrush(fillColor)) g.FillPath(br, p);
                }
                // 挖掉內圈 → 變成弧帶
                using (System.Drawing.Drawing2D.GraphicsPath p2 = new System.Drawing.Drawing2D.GraphicsPath())
                {
                    p2.AddPie(inner.X, inner.Y, inner.Width, inner.Height, start, sweep);
                    using (SolidBrush br2 = new SolidBrush(Color.Transparent)) g.FillPath(br2, p2);
                }
            }
            // 套遮罩
            ApplyMaskAlpha(fill, mask);

            // 合成到底圖（再縮放到目標）
            Bitmap composed = new Bitmap(track.Width, track.Height);
            using (Graphics g2 = Graphics.FromImage(composed))
            {
                g2.Clear(Color.Transparent);
                g2.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;

                g2.DrawImage(track, 0, 0);
                g2.DrawImage(fill, 0, 0);
                g2.DrawImage(edge, 0, 0);
            }

            Bitmap bmpTarget = new Bitmap(target.Width, target.Height);
            using (Graphics g3 = Graphics.FromImage(bmpTarget))
            {
                g3.Clear(Color.Transparent);
                g3.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g3.DrawImage(composed, new Rectangle(0, 0, bmpTarget.Width, bmpTarget.Height));
            }

            Image old = target.Image;
            target.Image = bmpTarget;
            if (old != null) old.Dispose();
            composed.Dispose();
            fill.Dispose();
        }

        private void AdsPoll_Tick(object sender, EventArgs e)
        {
            if (!_adsConnected) return;

            try
            {
                float tempC = _ads.ReadAny<float>(hTemp);
                float WaterLevel = _ads.ReadAny<float>(hWaterLevel);
                float press = _ads.ReadAny<float>(hPress);
                bool diFl = _ads.ReadAny<bool>(hDiFlame);
                bool diDoorV = _ads.ReadAny<bool>(hDiDoor);
                //bool diLowV = (bool)_ads.ReadAny(hDiLow, typeof(bool));

                bool doBl = _ads.ReadAny<bool>(hDoBlower); // TRUE=close(1)
                bool doGas = _ads.ReadAny<bool>(hDoGas);
                bool doCircV = _ads.ReadAny<bool>(hDoCirc);
                bool doFeedV = _ads.ReadAny<bool>(hDoFeed);

                // 映射到你既有邏輯
                diFlame = diFl;
                diDoor = diDoorV;
                //diLowWater = diLowV;

                doBlower = doBl ? 1 : 0;
                doGasValve = doGas ? 1 : 0;
                doCirc = doCircV ? 1 : 0;
                doFeed = doFeedV ? 1 : 0;

                // ---- 畫面 ----
                UpdateFanAnimation();   // ← 依 PLC 真實狀態開/停動畫
                UpdateGasValveImage();         // ← 依 PLC 真實狀態更新圖示
                // 溫度計（你已有的函式；顏色可依喜好）
                RenderThermometerTo(pbThermoShell, WaterLevel * 10, Color.FromArgb(255, 255, 120, 0));

                // 儀表：Temp 0..120、Press 0..10 → 映射到 0..1
                double rTemp = Math.Max(0, Math.Min(1, tempC / 120.0));
                double rPress = Math.Max(0, Math.Min(1, (press - 10.0) / 10));
                RenderGaugeTo(pbGaugeTempOverlay, rTemp, Color.Orange);
                RenderGaugeTo(pbGaugePressOverlay, rPress, Color.DeepSkyBlue);

                // 數字與 DI/卡片
                lblGaugeTempValue.Text = tempC.ToString("0.0") + " °C";
                lblGaugePressValue.Text = press.ToString("0.0") + " mA";
                SyncDI();
                UpdateInfoCards();
            }
            catch (AdsErrorException)
            {
                if (_isClosing) return;          // 關閉中就不要重連
                AdsDisconnect();

                var t = new Timer { Interval = 2000 };
                t.Tick += (s, e2) =>
                {
                    var self = (Timer)s;
                    self.Stop(); self.Dispose();
                    _retryTimers.Remove(self);
                    if (!_isClosing) AdsConnect();
                };
                _retryTimers.Add(t);
                t.Start();
            }
            catch { /* 其它例外視需要紀錄 */ }
        }

        // 0=open、1=close → 對應不同圖片
        private void UpdateGasValveImage()
        {
            pbGasValve.Image = (doGasValve == 0)
                ? Properties.Resources.valve_close   // 關：顯示關閉圖
                : Properties.Resources.valve_open;   // 開：顯示開啟圖
        }

        private void UpdateFanAnimation()
        {
            bool fanOn = (doBlower == 1);   // 1=open → 開的時候轉

            if (fanOn)
            {
                if (!_fanTimer.Enabled)
                {
                    _fanFrameIdx = 0;
                    pbBlower.Image = _fanFrames[_fanFrameIdx];
                    _fanTimer.Interval = 80;    // 轉速
                    _fanTimer.Start();
                }
            }
            else
            {
                if (_fanTimer.Enabled) _fanTimer.Stop();
                _fanFrameIdx = 0;
                pbBlower.Image = _fanFrames[0]; // 停在第一張
            }
        }

        private void lblConn_Click(object sender, EventArgs e)
        {
            if (_adsConnected) AdsDisconnect();
            else AdsConnect();
        }

        private void UpdateConnUI(bool ok)
        {
            if (ok)
            {
                lblAdsState.Text = "●Connected";
                lblAdsState.ForeColor = Color.Lime;
            }
            else
            {
                lblAdsState.Text = "●Disconnected";
                lblAdsState.ForeColor = Color.OrangeRed;
            }
        }

        private void AdsConnect()
        {
            try
            {
                _ads?.Dispose();
                _ads = new AdsClientEx(s => Debug.WriteLine("[ADS] " + s));

                // 先 851，不行再 852
                if (!_ads.Connect("auto", 851) && !_ads.Connect("auto", 852))
                {
                    throw new InvalidOperationException("ADS not connected. 請檢查 TwinCAT 是否 RUN / 路由 / 防火牆。");
                }

                // 逐一建立 handle；收集不存在的符號
                var missing = new List<string>();

                hTemp = TryCreate("GVL.Sensor", missing);
                hWaterLevel = TryCreate("GVL.In3238_1", missing);
                hPress = TryCreate("GVL.In3438_1", missing);
                hDoBlower = TryCreate("GVL.Output_1", missing);
                hDoGas = TryCreate("GVL.Output_5", missing);
                hDoCirc = TryCreate("GVL.Output_3", missing);
                hDoFeed = TryCreate("GVL.Output_4", missing);
                hDiFlame = TryCreate("GVL.Output_2", missing);
                hDiDoor = TryCreate("GVL.Output_6", missing);
                hFire = TryCreate("GVL.bFireDetected", missing);

                if (missing.Count > 0)
                {
                    MessageBox.Show(
                        "以下符號在 PLC 符號表找不到：\n• " +
                        string.Join("\n• ", missing) +
                        "\n\n請確認：\n" +
                        "1) 變數真的在 GVL，名字完全一致（可能需要加 PLC1.GVL.…）。\n" +
                        "2) 專案 Build 有產生 Symbol File，且 Download=All。\n" +
                        "3) 變數屬性 Link always / 未被最佳化移除。\n" +
                        "4) 已 Activate Configuration → Login → Run。\n" +
                        "5) 目前連到的埠是正確的（851/852）。",
                        "DeviceSymbolNotFound (0x710)");
                    // 仍可讓 UI 顯示 Disconnected 狀態
                }

                _adsPoll.Tick -= AdsPoll_Tick;
                _adsPoll.Tick += AdsPoll_Tick;
                _adsPoll.Start();

                _adsConnected = _ads.IsOk;
                UpdateConnUI(_adsConnected);
            }
            catch (Exception ex)
            {
                _adsConnected = false;
                UpdateConnUI(false);
                if (!_isClosing) MessageBox.Show("ADS connect failed: " + ex.Message);
            }
        }

        private uint TryCreate(string path, List<string> missing)
        {
            try { return _ads.CreateHandle(path); }
            catch (TwinCAT.Ads.AdsErrorException ex) { missing.Add($"{path} -> {ex.ErrorCode}"); return 0; }
            catch (Exception ex) { missing.Add($"{path} -> {ex.Message}"); return 0; }
        }


        private void AdsDisconnect(bool fast = false)
        {
            try { _adsPoll.Stop(); } catch { }

            if (_ads != null)
            {
                // 刪 handle（包裝類本身不會自動幫你刪）
                foreach (var h in new[] { hTemp, hWaterLevel, hPress, hDoBlower, hDoGas, hDoCirc, hDoFeed, hDiFlame, hDiDoor, hDiLow, hFire })
                    try { _ads.DeleteHandle(h); } catch { }
                hTemp = hWaterLevel = hPress = hDoBlower = hDoGas = hDoCirc = hDoFeed = hDiFlame = hDiDoor = hDiLow = hFire = 0;

                try { _ads.Dispose(); } catch { }
                _ads = null;
            }

            _adsConnected = false;
            if (!_isClosing) UpdateConnUI(false);

            if (_fanTimer.Enabled) _fanTimer.Stop();
            _fanFrameIdx = 0;
            pbBlower.Image = _fanFrames[0];
            UpdateGasValveImage();
        }

        // 只在狀態改變時寫入 PLC；失敗就略過，等下次變化再寫
        // 寫入火焰位元：
        private void WriteFireToAds(bool fire)
        {
            if (!_adsConnected || _ads == null || hFire == 0 || _isClosing) return;
            if (fire == _lastFireSent) return;
            try { _ads.WriteBool(hFire, fire); _lastFireSent = fire; }
            catch { _adsConnected = false; }
        }

        private void PbCameraView_Click(object sender, EventArgs e)
        {
            if (_camOn) StopCamera();
            else StartCamera();
        }

        private void StartCamera()
        {
            try
            {
                // 若你要用 RTSP/USB 路徑，也可改成：_cap = new VideoCapture("rtsp://....");
                _cap = new VideoCapture(_camIndex);
                if (!_cap.IsOpened())
                {
                    MessageBox.Show("Camera open failed.");
                    SafeSetFire(false);
                    return;
                }

                // 想指定解析度（視攝影機支援而定）
                _cap.Set(VideoCaptureProperties.FrameWidth, 1280);
                _cap.Set(VideoCaptureProperties.FrameHeight, 720);

                _camOn = true;
                _camTimer.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Camera error: " + ex.Message);
                SafeSetFire(false);
                StopCamera();
            }
        }

        private void StopCamera()
        {
            try { _camTimer.Stop(); } catch { }

            if (pbCameraView.Image != null)
            {
                try { pbCameraView.Image.Dispose(); } catch { }
                pbCameraView.Image = null;
            }

            if (_cap != null)
            {
                try { _cap.Release(); _cap.Dispose(); } catch { }
                _cap = null;
            }

            _prevGray?.Dispose();
            _prevGray = null;

            _camOn = false;
            SafeSetFire(false);
        }

        private void CamTimer_Tick(object sender, EventArgs e)
        {
            if (_cap == null || !_cap.IsOpened())
            {
                SafeSetFire(false);
                return;
            }

            OCV.Mat frame = new OCV.Mat();
            try
            {
                if (!_cap.Read(frame) || frame.Empty())
                {
                    SafeSetFire(false);
                    return;
                }

                // 偵測火焰
                CvRect box;
                bool hasFire = DetectFire(frame, out box);
                // 用去抖/延遲關閉，取得「穩定」結果
                bool fireStable = UpdateFireStable(hasFire, box);

                // 畫框建議也跟著用穩定框（可選）：
                if (fireStable && _lastBox.Width > 0 && _lastBox.Height > 0)
                {
                    var fireColor = new OCV.Scalar(0, 165, 255);
                    OCV.Cv2.Rectangle(frame, _lastBox, fireColor, 2);
                    OCV.Cv2.PutText(frame, "FIRE",
                        new OCV.Point(_lastBox.X, Math.Max(0, _lastBox.Y - 6)),
                        OCV.HersheyFonts.HersheySimplex, 0.7, fireColor, 2);
                }

                // 顯示到 PictureBox
                System.Drawing.Bitmap bmp = BitmapConverter.ToBitmap(frame);
                var old = pbCameraView.Image;
                pbCameraView.Image = bmp;
                if (old != null) old.Dispose();

                // 更新告警 Label
                SafeSetFire(fireStable);
            }
            catch
            {
                SafeSetFire(false);
            }
            finally
            {
                frame.Dispose();
            }
        }

        private bool DetectFire(OCV.Mat bgrFrame, out OCRect fireBox)
        {
            fireBox = new OCRect();
            if (bgrFrame == null || bgrFrame.Empty()) return false;

            // 1) 縮小加速
            const double scale = 0.6;
            var smallSize = new CvSize((int)(bgrFrame.Width * scale), (int)(bgrFrame.Height * scale));
            using var small = new OCV.Mat();
            OCV.Cv2.Resize(bgrFrame, small, smallSize, 0, 0, OCV.InterpolationFlags.Area);

            // 2) 轉 HSV、拆 S/V 方便做「超亮火焰」
            using var hsv = new OCV.Mat();
            OCV.Cv2.CvtColor(small, hsv, OCV.ColorConversionCodes.BGR2HSV);
            var hsvSplit = OCV.Cv2.Split(hsv);
            using var S = hsvSplit[1];
            using var V = hsvSplit[2];
            hsvSplit[0].Dispose();   // ← 釋放 H，避免資源外洩

            // 紅/橘/黃（把黃火加進來）
            using var maskR1 = new OCV.Mat(); // 0~15
            using var maskR2 = new OCV.Mat(); // 160~180
            using var maskY = new OCV.Mat(); // 12~40：打火機常見黃火
            OCV.Cv2.InRange(hsv, new OCV.Scalar(0, 140, 140), new OCV.Scalar(15, 255, 255), maskR1);
            OCV.Cv2.InRange(hsv, new OCV.Scalar(160, 140, 140), new OCV.Scalar(180, 255, 255), maskR2);
            OCV.Cv2.InRange(hsv, new OCV.Scalar(12, 120, 140), new OCV.Scalar(40, 255, 255), maskY);

            using var maskHSV = new OCV.Mat();
            OCV.Cv2.BitwiseOr(maskR1, maskR2, maskHSV);
            OCV.Cv2.BitwiseOr(maskHSV, maskY, maskHSV);

            // 3) YCrCb：Cr 高（偏紅/橘）——對黃火較不敏感，所以僅作其中一路
            using var ycc = new OCV.Mat();
            OCV.Cv2.CvtColor(small, ycc, OCV.ColorConversionCodes.BGR2YCrCb);
            var yccSplit = OCV.Cv2.Split(ycc);
            using var Cr = yccSplit[1];
            using var maskCr = new OCV.Mat();
            OCV.Cv2.Threshold(Cr, maskCr, 150, 255, OCV.ThresholdTypes.Binary);
            foreach (var c in yccSplit) c.Dispose();

            // 4) 「超亮火焰」：S 高且 V 很高（用來放寬對小黃火的要求）
            using var sHi = new OCV.Mat(); OCV.Cv2.Threshold(S, sHi, 120, 255, OCV.ThresholdTypes.Binary);
            using var vHi = new OCV.Mat(); OCV.Cv2.Threshold(V, vHi, 200, 255, OCV.ThresholdTypes.Binary);
            using var maskSVBright = new OCV.Mat();
            OCV.Cv2.BitwiseAnd(sHi, vHi, maskSVBright);

            // 5) 顏色整合：
            //    路徑A：暖色HSV AND Cr(偏紅/橘)
            //    路徑B：黃色 AND 超亮SV（補打火機黃火）
            using var warmA = new OCV.Mat();
            OCV.Cv2.BitwiseAnd(maskHSV, maskCr, warmA);

            using var warmB = new OCV.Mat();
            OCV.Cv2.BitwiseAnd(maskY, maskSVBright, warmB);

            using var maskColor = new OCV.Mat();
            OCV.Cv2.BitwiseOr(warmA, warmB, maskColor);

            // 6) 動態門檻（沒有晃動就以「超亮SV」作為替代條件）
            using var gray = new OCV.Mat();
            OCV.Cv2.CvtColor(small, gray, OCV.ColorConversionCodes.BGR2GRAY);

            using var motion = new OCV.Mat();
            bool hasPrev = _prevGray != null && !_prevGray.Empty();
            if (hasPrev)
            {
                using var diff = new OCV.Mat();
                OCV.Cv2.Absdiff(gray, _prevGray, diff);
                OCV.Cv2.GaussianBlur(diff, diff, new CvSize(5, 5), 0);
                OCV.Cv2.Threshold(diff, motion, 14, 255, OCV.ThresholdTypes.Binary); // 比原先更敏感一些
            }
            else
            {
                motion.Create(gray.Size(), OCV.MatType.CV_8UC1);
                motion.SetTo(OCV.Scalar.All(255)); // 第一幀放行
            }

            using var gate = new OCV.Mat();
            OCV.Cv2.BitwiseOr(motion, maskSVBright, gate); // 有晃或超亮 其一即可

            using var mask = new OCV.Mat();
            OCV.Cv2.BitwiseAnd(maskColor, gate, mask);

            // 7) 形態學淨化（偏小火 → kernel 裡一點點就好）
            using var k3 = OCV.Cv2.GetStructuringElement(OCV.MorphShapes.Rect, new CvSize(3, 3));
            OCV.Cv2.MorphologyEx(mask, mask, OCV.MorphTypes.Open, k3, iterations: 1);
            OCV.Cv2.Dilate(mask, mask, k3, iterations: 1);

            // 8) 等高線
            OCV.Cv2.FindContours(mask, out OCPt[][] contours, out OCV.HierarchyIndex[] _,
                OCV.RetrievalModes.External, OCV.ContourApproximationModes.ApproxSimple);

            // 9) 幾何過濾（對小面積放寬）
            double bestArea = 0;
            OCRect bestRect = new OCRect();
            int totalPx = small.Width * small.Height;
            double minArea = Math.Max(50, totalPx * 0.00015); // ↓ 0.015% 允許很小的火點
            double maxArea = totalPx * 0.35;

            foreach (var c in contours)
            {
                double area = OCV.Cv2.ContourArea(c);
                if (area < minArea || area > maxArea) continue;

                var rect = OCV.Cv2.BoundingRect(c);
                if (rect.Width <= 2 || rect.Height <= 2) continue;

                using (var mroi = new OCV.Mat(motion, rect))
                {
                    double moveRatio = OCV.Cv2.CountNonZero(mroi) / (double)(rect.Width * rect.Height);
                    if (moveRatio < 0.06) continue;  // 可調：越大越嚴
                }

                using (var vRoi = new OCV.Mat(V, rect))   // V 來自前面 HSV 拆出的第三個通道
                {
                    OCV.Cv2.MeanStdDev(vRoi, out var mean, out var std);
                    if (std.Val0 < 10) continue; // 可調：越大越嚴；10~15 常見
                }

                // 小火：僅驗證「偏縱向」即可
                if (area < minArea * 3)
                {
                    double ar = rect.Height / (double)rect.Width; // 縱向 > 橫向
                    if (ar < 1.1 || ar > 8.0) continue;
                }
                else
                {
                    // 正常幾何條件（放寬）
                    double peri = OCV.Cv2.ArcLength(c, true);
                    double circularity = 4.0 * Math.PI * area / (peri * peri + 1e-6);
                    if (circularity > 0.93) continue; // 太圓像燈泡

                    var hull = OCV.Cv2.ConvexHull(c);
                    double hullArea = OCV.Cv2.ContourArea(hull);
                    double solidity = area / (hullArea + 1e-6);
                    if (solidity > 0.98 || solidity < 0.30) continue;

                    double extent = area / (rect.Width * rect.Height + 1e-6);
                    if (extent > 0.90 || extent < 0.15) continue;
                }

                if (area > bestArea) { bestArea = area; bestRect = rect; }
            }

            // 更新上一幀
            _prevGray?.Dispose();
            _prevGray = gray.Clone();

            if (bestArea > 0)
            {
                fireBox = new OCRect(
                    (int)(bestRect.X / scale),
                    (int)(bestRect.Y / scale),
                    (int)(bestRect.Width / scale),
                    (int)(bestRect.Height / scale));
                return true;
            }
            return false;
        }

        private bool UpdateFireStable(bool hitNow, OCRect boxNow)
        {
            // 先把本幀結果塞進佇列
            _fireQ.Enqueue(hitNow);
            while (_fireQ.Count > N_FRAMES) _fireQ.Dequeue();

            int trueCnt = _fireQ.Count(b => b);

            if (!_fireStable)
            {
                // 還沒開：最近 N 幀命中數夠，就「開」
                if (trueCnt >= K_ON)
                {
                    _fireStable = true;
                    _cooldown = HOLD_OFF;
                    if (hitNow) _lastBox = boxNow;  // 記住當前框
                }
            }
            else
            {
                // 已經開：只要本幀仍命中就重置冷卻；沒命中就遞減，歸零才關閉
                if (hitNow)
                {
                    _cooldown = HOLD_OFF;
                    _lastBox = boxNow;
                }
                else
                {
                    if (_cooldown > 0) _cooldown--;
                    if (_cooldown <= 0 && trueCnt <= 1) // 觀察窗也幾乎都沒命中
                        _fireStable = false;
                }
            }
            return _fireStable;
        }

        private void SafeSetFire(bool on)
        {
            if (lblFire.InvokeRequired)
            {
                lblFire.BeginInvoke(new Action<bool>(SafeSetFire), on);
                return;
            }
            lblFire.Text = on ? "Flame: DETECTED" : "Flame: none";
            lblFire.BackColor = on ? System.Drawing.Color.OrangeRed
                                   : System.Drawing.Color.FromArgb(0x7A, 0xC1, 0x7A);

            // ★ 同步寫到 PLC（去抖：僅狀態變更時才寫）
            WriteFireToAds(on);
        }

        public sealed class AdsClientEx : IDisposable
        {
            private AdsClient _ads = new AdsClient();
            private readonly Action<string> _log;
            public AdsClientEx(Action<string> log = null) => _log = log ?? (_ => { });

            public bool IsOk { get; private set; }
            public string ConnectedNetId { get; private set; }
            public int ConnectedPort { get; private set; }

            private void Log(string s) { try { _log(s); } catch { } }

            public bool Connect(string amsNetIdOrAuto, int port = 851, bool throwOnFail = false)
            {
                //try { _ads?.Dispose(); } catch { }
                _ads = new AdsClient { Timeout = 800 };
                IsOk = false;

                string id = amsNetIdOrAuto;
                if (string.IsNullOrWhiteSpace(id) || id.Equals("auto", StringComparison.OrdinalIgnoreCase))
                {
                    id = ReadLocalAmsNetId();
                    if (string.IsNullOrWhiteSpace(id))
                    {
                        id = "127.0.0.1.1.1";
                        Log("Local AMS Net ID 未從登錄檔取得，改用 127.0.0.1.1.1");
                    }
                }

                try
                {
                    _ads.Connect(id, port);      // ★關鍵：明確 NetId + Port
                    _ads.ReadState();            // 健康檢查
                    ConnectedNetId = id;
                    ConnectedPort = port;
                    IsOk = true;
                    Log($"ADS Connect OK id={id}, port={port}, client={_ads.ClientAddress?.NetId}");
                    return true;
                }
                catch (Exception ex)
                {
                    Log($"ADS Connect ERROR: {ex.Message}");
                    if (throwOnFail) throw;
                    return false;
                }
            }

            public static string ReadLocalAmsNetId()
            {
                try
                {
                    using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Beckhoff\TwinCAT3\System"))
                        if (k?.GetValue("AmsNetId") is string s && !string.IsNullOrWhiteSpace(s)) return s;
                    using (var k2 = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Beckhoff\TwinCAT3\System"))
                        if (k2?.GetValue("AmsNetId") is string s2 && !string.IsNullOrWhiteSpace(s2)) return s2;
                }
                catch { }
                return null;
            }

            public uint CreateHandle(string symbolPath)
            {
                if (!IsOk) throw new InvalidOperationException("ADS not connected.");
                return _ads.CreateVariableHandle(symbolPath);
            }
            public void DeleteHandle(uint h)
            {
                if (h != 0) try { _ads.DeleteVariableHandle(h); } catch { }
            }
            public T ReadAny<T>(uint handle)
            {
                if (!IsOk) throw new InvalidOperationException("ADS not connected.");
                return (T)_ads.ReadAny(handle, typeof(T));
            }
            public void WriteAny<T>(uint handle, T value)
            {
                if (!IsOk) throw new InvalidOperationException("ADS not connected.");
                _ads.WriteAny(handle, value);
            }
            public void WriteBool(uint handle, bool value) => WriteAny(handle, value);

            public void Dispose()
            {
                try { _ads?.Dispose(); } catch { }
                _ads = null;
                IsOk = false;
            }
        }
    }

    internal static class NativeBootstrap
    {
        [DllImport("kernel32", SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);

        private static string _nativeDir;

        public static void EnsureOpenCvNativeReady()
        {
            // 建一個穩定可寫路徑：%LOCALAPPDATA%\DemoBox_Boiler\native\x64
            string baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DemoBox_Boiler", "native", Environment.Is64BitProcess ? "x64" : "x86");

            Directory.CreateDirectory(baseDir);

            string dllExtern = Path.Combine(baseDir, "OpenCvSharpExtern.dll");
            string dllFfmpeg = Path.Combine(baseDir, Environment.Is64BitProcess
                ? "opencv_videoio_ffmpeg4110_64.dll"
                : "opencv_videoio_ffmpeg4110.dll");

            // 寫出內嵌資源
            if (!File.Exists(dllExtern))
                File.WriteAllBytes(dllExtern, Properties.Resources.OpenCvSharpExtern_dll);

            if (!File.Exists(dllFfmpeg))
                File.WriteAllBytes(dllFfmpeg, Properties.Resources.opencv_ffmpeg64_dll);

            // 讓 DllImport 能找到
            SetDllDirectory(baseDir);
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            if (!path.Split(';').Contains(baseDir, StringComparer.OrdinalIgnoreCase))
                Environment.SetEnvironmentVariable("PATH", baseDir + ";" + path);

            _nativeDir = baseDir; // 如需除錯時查看
        }
    }
}
