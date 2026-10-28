using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CharacterType
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                CharacterForm.Log("Starting CharacterType application (Smart Auto-Switching)");
                Application.Run(new CharacterForm());
                CharacterForm.Log("Application exited cleanly");
            }
            catch (Exception ex)
            {
                CharacterForm.Log("Main Exception: " + ex);
                MessageBox.Show("오류가 발생했습니다:\n" + ex.Message, "CharacterType 오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    public class CharacterForm : Form
    {
        #region Win32 API Definitions

        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int HT_CAPTION = 0x2;

        private const int WH_KEYBOARD_LL = 13;
        private const int WH_MOUSE_LL = 14;

        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;

        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_RBUTTONUP = 0x0205;

        private const byte AC_SRC_OVER = 0x00;
        private const byte AC_SRC_ALPHA = 0x01;
        private const uint ULW_ALPHA = 0x02;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int x;
            public int y;
            public POINT(int x, int y) { this.x = x; this.y = y; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE
        {
            public int cx;
            public int cy;
            public SIZE(int cx, int cy) { this.cx = cx; this.cy = cy; }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, uint crKey, [In] ref BLENDFUNCTION pblend, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        #endregion

        // Singleton reference
        private static CharacterForm s_instance;
        private static HookProc s_kbdHookProc;
        private static IntPtr s_kbdHookId = IntPtr.Zero;
        private static HookProc s_mouseHookProc;
        private static IntPtr s_mouseHookId = IntPtr.Zero;

        // 6 Raw PNG Bitmaps
        private Bitmap rawBody;
        private Bitmap rawLeftArmReady;
        private Bitmap rawLeftArmType;
        private Bitmap rawRightArmReady;
        private Bitmap rawRightArmType;
        private Bitmap rawMouseWithArm;

        // 6 Scaled Bitmaps (Format32bppPArgb)
        private Bitmap scaledBody;
        private Bitmap scaledLeftArmReady;
        private Bitmap scaledLeftArmType;
        private Bitmap scaledRightArmReady;
        private Bitmap scaledRightArmType;
        private Bitmap scaledMouseWithArm;

        // Composite Drawing Buffer
        private Bitmap compositeBitmap;
        private Graphics compositeGraphics;

        // Native Resolution detected from PNG
        private int baseWidth = 453;
        private int baseHeight = 354;

        // Settings
        private float currentScale = 1.0f; // 100% default
        private float currentOpacity = 1.0f; // 100% default
        private bool isAlwaysOnTop = true;

        // Mode: Dual Keyboard vs Keyboard + Mouse
        public enum OperationMode { DualKeyboard, KeyboardAndMouse }
        private OperationMode currentMode = OperationMode.DualKeyboard;

        // Typing State & Re-trigger Bounce
        private bool isLeftTyping = false;
        private bool isRightTyping = false;
        private readonly HashSet<int> activePressedKeys = new HashSet<int>();
        private System.Windows.Forms.Timer leftBounceTimer;
        private System.Windows.Forms.Timer rightBounceTimer;
        public static int ReTriggerMs = 35;        // 35ms bounce lift on consecutive hits

        // Mouse Motion & Tracking Settings (User Editable via config.ini)
        public static float MaxRangeX = 10.0f;     // Horizontal movement clamp (px)
        public static float MaxRangeY = 6.0f;      // Vertical movement clamp (px)
        public static float Smoothing = 0.20f;     // Movement smoothing (0.05 ~ 0.5)
        public static float AutoSwitchDelay = 2.0f;// Seconds of mouse inactivity before returning to DualKeyboard

        // Mouse Real-Time Tracking State
        private POINT lastRawCursorPos;
        private float targetOffsetX = 0f;
        private float targetOffsetY = 0f;
        private float currentOffsetX = 0f;
        private float currentOffsetY = 0f;
        private bool isMousePressed = false;
        private Stopwatch mouseInactiveStopwatch = Stopwatch.StartNew();
        private System.Windows.Forms.Timer mouseTrackingTimer;

        // UI
        private ContextMenuStrip contextMenu;
        private ToolStripMenuItem menuScale;
        private ToolStripMenuItem menuOpacity;
        private ToolStripMenuItem menuAlwaysOnTop;
        private ToolStripMenuItem menuMouseRange;
        private NotifyIcon trayIcon;

        #region Keyboard Left/Right Partition Mapping

        private static readonly HashSet<int> LeftKeyCodes = new HashSet<int>
        {
            0x1B, // Esc
            0x70, 0x71, 0x72, 0x73, 0x74, 0x75, // F1 ~ F6
            0xC0, // ` ~
            0x31, 0x32, 0x33, 0x34, 0x35, // 1, 2, 3, 4, 5
            0x09, // Tab
            0x51, 0x57, 0x45, 0x52, 0x54, // Q, W, E, R, T
            0x14, // CapsLock
            0x41, 0x53, 0x44, 0x46, 0x47, // A, S, D, F, G
            0xA0, // LShift
            0x5A, 0x58, 0x43, 0x56, 0x42, // Z, X, C, V, B
            0xA2, // LCtrl
            0x5B, // LWin
            0xA4, // LAlt
            0x20  // Space
        };

        private static bool IsLeftKey(int vkCode, uint scanCode, bool isExtended)
        {
            if (isExtended) return false;
            if (vkCode == 0xA1 || scanCode == 0x36) return false; // Right Shift
            if (vkCode == 0xA0 || scanCode == 0x2A) return true;  // Left Shift
            if (vkCode == 0x10) return scanCode != 0x36;
            if (vkCode == 0x11) return !isExtended;
            if (vkCode == 0x12) return !isExtended;

            return LeftKeyCodes.Contains(vkCode);
        }

        #endregion

        public CharacterForm()
        {
            s_instance = this;
            Log("CharacterForm constructor");

            this.FormBorderStyle = FormBorderStyle.None;
            this.ShowInTaskbar = false;
            this.TopMost = true;
            this.StartPosition = FormStartPosition.Manual;

            LoadConfig();
            LoadImages();
            RescaleBitmaps();

            int initialW = (int)Math.Round(baseWidth * currentScale);
            int initialH = (int)Math.Round(baseHeight * currentScale);
            this.Size = new Size(initialW, initialH);

            Rectangle workingArea = Screen.PrimaryScreen.WorkingArea;
            this.Location = new Point(
                workingArea.Right - initialW - 30,
                workingArea.Bottom - initialH - 30
            );

            // Left & Right Re-trigger Bounce Timers
            leftBounceTimer = new System.Windows.Forms.Timer();
            leftBounceTimer.Interval = Math.Max(10, ReTriggerMs);
            leftBounceTimer.Tick += (s, e) =>
            {
                leftBounceTimer.Stop();
                if (HasLeftKeyPressed())
                {
                    isLeftTyping = true;
                    RenderLayers();
                }
            };

            rightBounceTimer = new System.Windows.Forms.Timer();
            rightBounceTimer.Interval = Math.Max(10, ReTriggerMs);
            rightBounceTimer.Tick += (s, e) =>
            {
                rightBounceTimer.Stop();
                if (HasRightKeyPressed())
                {
                    isRightTyping = true;
                    RenderLayers();
                }
            };

            // Mouse Real-Time Tracking & Auto-Switch Timer (60 FPS: 16ms)
            GetCursorPos(out lastRawCursorPos);
            mouseTrackingTimer = new System.Windows.Forms.Timer();
            mouseTrackingTimer.Interval = 16;
            mouseTrackingTimer.Tick += MouseTrackingTimer_Tick;
            mouseTrackingTimer.Start();

            SetupContextMenu();
            SetupTrayIcon();

            this.MouseDown += OnMouseDown;
            this.MouseUp += OnMouseUp;

            RenderLayers();
            InstallHooks();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED;
                cp.ExStyle |= WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        #region Config (config.ini) Load & Save

        private string GetConfigPath()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.ini");
        }

        private void LoadConfig()
        {
            string path = GetConfigPath();
            if (!File.Exists(path))
            {
                SaveConfig();
                return;
            }

            try
            {
                string[] lines = File.ReadAllLines(path);
                foreach (string line in lines)
                {
                    string trimmed = line.Trim();
                    if (trimmed.StartsWith(";") || trimmed.StartsWith("#") || !trimmed.Contains("="))
                        continue;

                    string[] parts = trimmed.Split(new char[] { '=' }, 2);
                    string key = parts[0].Trim();
                    string val = parts[1].Split(';')[0].Trim();

                    float parsedFloat;
                    if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out parsedFloat))
                    {
                        if (key.Equals("MaxRangeX", StringComparison.OrdinalIgnoreCase)) MaxRangeX = parsedFloat;
                        else if (key.Equals("MaxRangeY", StringComparison.OrdinalIgnoreCase)) MaxRangeY = parsedFloat;
                        else if (key.Equals("Smoothing", StringComparison.OrdinalIgnoreCase)) Smoothing = parsedFloat;
                        else if (key.Equals("AutoSwitchDelay", StringComparison.OrdinalIgnoreCase)) AutoSwitchDelay = parsedFloat;
                        else if (key.Equals("ReTriggerMs", StringComparison.OrdinalIgnoreCase)) ReTriggerMs = (int)parsedFloat;
                    }
                }
                Log(string.Format("Config loaded: MaxRangeX={0}, MaxRangeY={1}, Smoothing={2}, AutoSwitchDelay={3}",
                    MaxRangeX, MaxRangeY, Smoothing, AutoSwitchDelay));
            }
            catch (Exception ex)
            {
                Log("Failed to load config.ini: " + ex.Message);
            }
        }

        private void SaveConfig()
        {
            try
            {
                string path = GetConfigPath();
                using (StreamWriter sw = new StreamWriter(path, false, System.Text.Encoding.UTF8))
                {
                    sw.WriteLine("; ========================================================");
                    sw.WriteLine("; CharacterType 마우스 흔들림 및 스마트 전환 설정 파일");
                    sw.WriteLine("; ========================================================");
                    sw.WriteLine("[MouseSettings]");
                    sw.WriteLine(string.Format(CultureInfo.InvariantCulture, "MaxRangeX={0:0.0}       ; 가로 최대 이동 반경(px) - 작을수록 덜 흔들림", MaxRangeX));
                    sw.WriteLine(string.Format(CultureInfo.InvariantCulture, "MaxRangeY={0:0.0}       ; 세로 최대 이동 반경(px) - 작을수록 덜 흔들림", MaxRangeY));
                    sw.WriteLine(string.Format(CultureInfo.InvariantCulture, "Smoothing={0:0.00}      ; 움직임 부드러움 (0.05: 아주 부드러움 ~ 0.5: 빠름)", Smoothing));
                    sw.WriteLine(string.Format(CultureInfo.InvariantCulture, "AutoSwitchDelay={0:0.0} ; 마우스 멈췄을 때 키보드 모드로 복귀하는 시간(초)", AutoSwitchDelay));
                    sw.WriteLine(string.Format(CultureInfo.InvariantCulture, "ReTriggerMs={0}          ; 연속 타건 시 손을 살짝 들었다 치는 순간 반동 시간(ms, 25~45)", ReTriggerMs));
                }
                Log("Config saved to " + path);
            }
            catch (Exception ex)
            {
                Log("Failed to save config.ini: " + ex.Message);
            }
        }

        #endregion

        #region Resource Loading & Dynamic Sizing

        private void LoadImages()
        {
            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            string resDir = Path.Combine(appDir, "resources");

            rawBody = LoadBitmapSafe(Path.Combine(resDir, "body.png"));
            rawLeftArmReady = LoadBitmapSafe(Path.Combine(resDir, "leftArmReady.png"));
            rawLeftArmType = LoadBitmapSafe(Path.Combine(resDir, "leftArmType.png"));
            rawRightArmReady = LoadBitmapSafe(Path.Combine(resDir, "rightArmReady.png"));
            rawRightArmType = LoadBitmapSafe(Path.Combine(resDir, "rightArmType.png"));
            rawMouseWithArm = LoadBitmapSafe(Path.Combine(resDir, "mouseWithArm.png"));

            if (rawBody != null)
            {
                baseWidth = rawBody.Width;
                baseHeight = rawBody.Height;
                Log(string.Format("Detected Base Image Resolution: {0}x{1}", baseWidth, baseHeight));
            }
        }

        private Bitmap LoadBitmapSafe(string path)
        {
            if (!File.Exists(path))
            {
                Log("Warning: Image not found: " + path);
                return CreateFallbackBitmap(453, 354, Color.Transparent);
            }

            try
            {
                using (Bitmap src = new Bitmap(path))
                {
                    Bitmap copy = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
                    using (Graphics g = Graphics.FromImage(copy))
                    {
                        g.DrawImage(src, 0, 0, src.Width, src.Height);
                    }
                    return copy;
                }
            }
            catch (Exception ex)
            {
                Log("LoadBitmapSafe failed for " + path + ": " + ex.Message);
                return CreateFallbackBitmap(453, 354, Color.Transparent);
            }
        }

        private Bitmap CreateFallbackBitmap(int w, int h, Color color)
        {
            Bitmap b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.Clear(color);
            }
            return b;
        }

        private void RescaleBitmaps()
        {
            int w = (int)Math.Round(baseWidth * currentScale);
            int h = (int)Math.Round(baseHeight * currentScale);

            if (scaledBody != null) scaledBody.Dispose();
            if (scaledLeftArmReady != null) scaledLeftArmReady.Dispose();
            if (scaledLeftArmType != null) scaledLeftArmType.Dispose();
            if (scaledRightArmReady != null) scaledRightArmReady.Dispose();
            if (scaledRightArmType != null) scaledRightArmType.Dispose();
            if (scaledMouseWithArm != null) scaledMouseWithArm.Dispose();

            scaledBody = ScaleBitmap(rawBody, w, h);
            scaledLeftArmReady = ScaleBitmap(rawLeftArmReady, w, h);
            scaledLeftArmType = ScaleBitmap(rawLeftArmType, w, h);
            scaledRightArmReady = ScaleBitmap(rawRightArmReady, w, h);
            scaledRightArmType = ScaleBitmap(rawRightArmType, w, h);
            scaledMouseWithArm = ScaleBitmap(rawMouseWithArm, w, h);

            if (compositeBitmap != null) compositeBitmap.Dispose();
            if (compositeGraphics != null) compositeGraphics.Dispose();

            compositeBitmap = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            compositeGraphics = Graphics.FromImage(compositeBitmap);
            compositeGraphics.CompositingMode = CompositingMode.SourceOver;
            compositeGraphics.CompositingQuality = CompositingQuality.HighSpeed;
            compositeGraphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            compositeGraphics.PixelOffsetMode = PixelOffsetMode.Half;

            this.Size = new Size(w, h);
        }

        private Bitmap ScaleBitmap(Bitmap src, int width, int height)
        {
            Bitmap result = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(result))
            {
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.SmoothingMode = SmoothingMode.None;
                g.Clear(Color.Transparent);
                if (src != null)
                {
                    g.DrawImage(src, 0, 0, width, height);
                }
            }
            return result;
        }

        #endregion

        #region Layered Composition & Rendering

        private void RenderLayers()
        {
            if (compositeBitmap == null || compositeGraphics == null || this.IsDisposed || !this.IsHandleCreated)
                return;

            compositeGraphics.Clear(Color.Transparent);

            int w = compositeBitmap.Width;
            int h = compositeBitmap.Height;

            // 1. Mouse Arm Layer (BOTTOM / BEHIND BODY)
            if (currentMode == OperationMode.KeyboardAndMouse)
            {
                if (scaledMouseWithArm != null)
                {
                    float clickY = isMousePressed ? (2.0f * currentScale) : 0f;
                    int drawX = (int)Math.Round(currentOffsetX * currentScale);
                    int drawY = (int)Math.Round((currentOffsetY + clickY) * currentScale);

                    compositeGraphics.DrawImage(scaledMouseWithArm, drawX, drawY, w, h);
                }
            }

            // 2. Base Body Layer (MIDDLE - body & desk covers mouse arm's base!)
            if (scaledBody != null)
            {
                compositeGraphics.DrawImage(scaledBody, 0, 0, w, h);
            }

            // 3. Left Arm Layer (TOP - in front of desk/body)
            Bitmap leftArm = isLeftTyping ? scaledLeftArmType : scaledLeftArmReady;
            if (leftArm != null)
            {
                compositeGraphics.DrawImage(leftArm, 0, 0, w, h);
            }

            // 4. Right Arm Layer (TOP - in front of desk/body, only in DualKeyboard mode)
            if (currentMode == OperationMode.DualKeyboard)
            {
                Bitmap rightArm = isRightTyping ? scaledRightArmType : scaledRightArmReady;
                if (rightArm != null)
                {
                    compositeGraphics.DrawImage(rightArm, 0, 0, w, h);
                }
            }

            CommitLayeredBitmap(compositeBitmap);
        }

        private void CommitLayeredBitmap(Bitmap bitmap)
        {
            IntPtr screenDC = GetDC(IntPtr.Zero);
            IntPtr memDC = CreateCompatibleDC(screenDC);
            IntPtr hBitmap = IntPtr.Zero;
            IntPtr oldBitmap = IntPtr.Zero;

            try
            {
                hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
                oldBitmap = SelectObject(memDC, hBitmap);

                SIZE size = new SIZE(bitmap.Width, bitmap.Height);
                POINT pointSource = new POINT(0, 0);
                POINT topPos = new POINT(this.Left, this.Top);

                BLENDFUNCTION blend = new BLENDFUNCTION
                {
                    BlendOp = AC_SRC_OVER,
                    BlendFlags = 0,
                    SourceConstantAlpha = (byte)Math.Round(currentOpacity * 255),
                    AlphaFormat = AC_SRC_ALPHA
                };

                UpdateLayeredWindow(
                    this.Handle,
                    screenDC,
                    ref topPos,
                    ref size,
                    memDC,
                    ref pointSource,
                    0,
                    ref blend,
                    ULW_ALPHA
                );
            }
            finally
            {
                if (oldBitmap != IntPtr.Zero) SelectObject(memDC, oldBitmap);
                if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
                DeleteDC(memDC);
                ReleaseDC(IntPtr.Zero, screenDC);
            }
        }

        #endregion

        #region Smart Auto-Switching & Mouse Tracking Loop

        private void MouseTrackingTimer_Tick(object sender, EventArgs e)
        {
            POINT curPos;
            if (!GetCursorPos(out curPos)) return;

            int dx = curPos.x - lastRawCursorPos.x;
            int dy = curPos.y - lastRawCursorPos.y;

            bool isMoving = (Math.Abs(dx) > 1 || Math.Abs(dy) > 1 || isMousePressed);

            if (isMoving)
            {
                lastRawCursorPos = curPos;
                mouseInactiveStopwatch.Restart();

                // Switch to Keyboard+Mouse mode when mouse moves!
                if (currentMode != OperationMode.KeyboardAndMouse)
                {
                    currentMode = OperationMode.KeyboardAndMouse;
                    Log("Smart Switch -> KeyboardAndMouse Mode");
                }

                // Map global screen coordinates to mouse pad target offset
                Rectangle screenBounds = Screen.PrimaryScreen.Bounds;
                float normX = ((float)curPos.x / Math.Max(1, screenBounds.Width)) - 0.5f;   // -0.5 ~ +0.5
                float normY = ((float)curPos.y / Math.Max(1, screenBounds.Height)) - 0.5f;  // -0.5 ~ +0.5

                targetOffsetX = Math.Max(-MaxRangeX, Math.Min(MaxRangeX, normX * (MaxRangeX * 2.0f)));
                targetOffsetY = Math.Max(-MaxRangeY, Math.Min(MaxRangeY, normY * (MaxRangeY * 2.0f)));
            }

            // Smooth Lerp Interpolation
            float diffX = targetOffsetX - currentOffsetX;
            float diffY = targetOffsetY - currentOffsetY;
            bool offsetChanged = false;

            if (Math.Abs(diffX) > 0.05f || Math.Abs(diffY) > 0.05f)
            {
                currentOffsetX += diffX * Smoothing;
                currentOffsetY += diffY * Smoothing;
                offsetChanged = true;
            }

            // Auto-Switch back to DualKeyboard mode when mouse is inactive
            if (currentMode == OperationMode.KeyboardAndMouse && !isMousePressed)
            {
                if (mouseInactiveStopwatch.Elapsed.TotalSeconds >= AutoSwitchDelay)
                {
                    currentMode = OperationMode.DualKeyboard;
                    targetOffsetX = 0f;
                    targetOffsetY = 0f;
                    currentOffsetX = 0f;
                    currentOffsetY = 0f;
                    Log("Smart Switch -> Reverted to DualKeyboard Mode");
                    RenderLayers();
                    return;
                }
            }

            if (currentMode == OperationMode.KeyboardAndMouse && offsetChanged)
            {
                RenderLayers();
            }
        }

        #endregion

        #region Keyboard & Mouse Event Handling

        private void InstallHooks()
        {
            using (Process curProcess = Process.GetCurrentProcess())
            using (ProcessModule curModule = curProcess.MainModule)
            {
                IntPtr hMod = GetModuleHandle(curModule.ModuleName);

                s_kbdHookProc = LowLevelKeyboardCallback;
                s_kbdHookId = SetWindowsHookEx(WH_KEYBOARD_LL, s_kbdHookProc, hMod, 0);

                s_mouseHookProc = LowLevelMouseCallback;
                s_mouseHookId = SetWindowsHookEx(WH_MOUSE_LL, s_mouseHookProc, hMod, 0);
            }
        }

        private static IntPtr LowLevelKeyboardCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && s_instance != null && !s_instance.IsDisposed)
            {
                int msg = wParam.ToInt32();
                KBDLLHOOKSTRUCT hookStruct = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));

                int vkCode = (int)hookStruct.vkCode;
                uint scanCode = hookStruct.scanCode;
                bool isExtended = (hookStruct.flags & 0x01) != 0;

                if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                {
                    s_instance.HandleKeyDown(vkCode, scanCode, isExtended);
                }
                else if (msg == WM_KEYUP || msg == WM_SYSKEYUP)
                {
                    s_instance.HandleKeyUp(vkCode);
                }
            }
            return CallNextHookEx(s_kbdHookId, nCode, wParam, lParam);
        }

        private static IntPtr LowLevelMouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && s_instance != null && !s_instance.IsDisposed)
            {
                int msg = wParam.ToInt32();
                MSLLHOOKSTRUCT hookStruct = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));

                if (msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN)
                {
                    s_instance.HandleMouseDownGlobal(hookStruct.pt);
                }
                else if (msg == WM_LBUTTONUP || msg == WM_RBUTTONUP)
                {
                    s_instance.HandleMouseUpGlobal();
                }
            }
            return CallNextHookEx(s_mouseHookId, nCode, wParam, lParam);
        }

        private bool HasLeftKeyPressed()
        {
            foreach (int k in activePressedKeys)
            {
                if (LeftKeyCodes.Contains(k)) return true;
            }
            return false;
        }

        private bool HasRightKeyPressed()
        {
            foreach (int k in activePressedKeys)
            {
                if (!LeftKeyCodes.Contains(k)) return true;
            }
            return false;
        }

        private void TriggerLeftStroke()
        {
            if (isLeftTyping)
            {
                // Already down: quick bounce lift (35ms) then slam down
                isLeftTyping = false;
                RenderLayers();
                leftBounceTimer.Stop();
                leftBounceTimer.Interval = Math.Max(10, ReTriggerMs);
                leftBounceTimer.Start();
            }
            else
            {
                isLeftTyping = true;
                leftBounceTimer.Stop();
                RenderLayers();
            }
        }

        private void TriggerRightStroke()
        {
            if (isRightTyping)
            {
                // Already down: quick bounce lift (35ms) then slam down
                isRightTyping = false;
                RenderLayers();
                rightBounceTimer.Stop();
                rightBounceTimer.Interval = Math.Max(10, ReTriggerMs);
                rightBounceTimer.Start();
            }
            else
            {
                isRightTyping = true;
                rightBounceTimer.Stop();
                RenderLayers();
            }
        }

        private void HandleKeyDown(int vkCode, uint scanCode, bool isExtended)
        {
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(() => HandleKeyDown(vkCode, scanCode, isExtended)));
                return;
            }

            bool isRepeat = activePressedKeys.Contains(vkCode);
            activePressedKeys.Add(vkCode);

            bool isLeft = IsLeftKey(vkCode, scanCode, isExtended);

            if (currentMode == OperationMode.KeyboardAndMouse)
            {
                // In Mouse mode, left hand handles typing
                if (isLeft)
                {
                    if (isRepeat) isLeftTyping = true; // Hold key: maintain pressed posture without fluttering!
                    else TriggerLeftStroke();          // New key: dynamic stroke bounce!
                }
                else
                {
                    // If user presses right-hand keys, return to DualKeyboard mode
                    currentMode = OperationMode.DualKeyboard;
                    if (isRepeat) isRightTyping = true;
                    else TriggerRightStroke();
                    Log("Keyboard typing -> Returned to DualKeyboard Mode");
                }
            }
            else
            {
                // In DualKeyboard mode: left key -> left hand, right key -> right hand
                if (isLeft)
                {
                    if (isRepeat) isLeftTyping = true; // Hold key: maintain pressed posture without fluttering!
                    else TriggerLeftStroke();          // New key: dynamic stroke bounce!
                }
                else
                {
                    if (isRepeat) isRightTyping = true; // Hold key: maintain pressed posture without fluttering!
                    else TriggerRightStroke();          // New key: dynamic stroke bounce!
                }
            }
        }

        private void HandleKeyUp(int vkCode)
        {
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(() => HandleKeyUp(vkCode)));
                return;
            }

            activePressedKeys.Remove(vkCode);

            bool hasLeft = HasLeftKeyPressed();
            bool hasRight = HasRightKeyPressed();

            if (!hasLeft)
            {
                leftBounceTimer.Stop();
                isLeftTyping = false;
            }

            if (!hasRight)
            {
                rightBounceTimer.Stop();
                isRightTyping = false;
            }

            RenderLayers();
        }

        private void HandleMouseDownGlobal(POINT pt)
        {
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(() => HandleMouseDownGlobal(pt)));
                return;
            }

            // Check if click is on our widget (for dragging/context menu)
            RECT r;
            GetWindowRect(this.Handle, out r);
            if (pt.x >= r.Left && pt.x <= r.Right && pt.y >= r.Top && pt.y <= r.Bottom)
            {
                return;
            }

            isMousePressed = true;
            mouseInactiveStopwatch.Restart();

            if (currentMode != OperationMode.KeyboardAndMouse)
            {
                currentMode = OperationMode.KeyboardAndMouse;
                Log("Mouse Click -> KeyboardAndMouse Mode");
            }

            RenderLayers();
        }

        private void HandleMouseUpGlobal()
        {
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(HandleMouseUpGlobal));
                return;
            }

            if (isMousePressed)
            {
                isMousePressed = false;
                RenderLayers();
            }
        }

        #endregion

        #region UI Context Menu

        private void SetupContextMenu()
        {
            contextMenu = new ContextMenuStrip();

            // Scale Submenu
            menuScale = new ToolStripMenuItem("크기 배율 (Scale)");
            float[] scales = new float[] { 0.5f, 0.75f, 1.0f, 1.25f, 1.5f, 2.0f, 3.0f };
            string[] scaleLabels = new string[] { "50%", "75%", "100% (기본값)", "125%", "150%", "200%", "300%" };

            for (int i = 0; i < scales.Length; i++)
            {
                float s = scales[i];
                ToolStripMenuItem item = new ToolStripMenuItem(scaleLabels[i]);
                item.Checked = Math.Abs(currentScale - s) < 0.01f;
                item.Click += (sender, e) =>
                {
                    currentScale = s;
                    foreach (ToolStripMenuItem mi in menuScale.DropDownItems)
                        mi.Checked = (mi == item);

                    Point prevCenter = new Point(this.Left + this.Width / 2, this.Top + this.Height / 2);
                    RescaleBitmaps();
                    this.Left = prevCenter.X - this.Width / 2;
                    this.Top = prevCenter.Y - this.Height / 2;
                    RenderLayers();
                };
                menuScale.DropDownItems.Add(item);
            }
            contextMenu.Items.Add(menuScale);

            // Opacity Submenu
            menuOpacity = new ToolStripMenuItem("투명도 (Opacity)");
            float[] opacities = new float[] { 1.0f, 0.8f, 0.6f, 0.4f, 0.2f };
            string[] opacityLabels = new string[] { "100% (불투명)", "80%", "60%", "40%", "20%" };

            for (int i = 0; i < opacities.Length; i++)
            {
                float op = opacities[i];
                ToolStripMenuItem item = new ToolStripMenuItem(opacityLabels[i]);
                item.Checked = Math.Abs(currentOpacity - op) < 0.01f;
                item.Click += (sender, e) =>
                {
                    currentOpacity = op;
                    foreach (ToolStripMenuItem mi in menuOpacity.DropDownItems)
                        mi.Checked = (mi == item);

                    RenderLayers();
                };
                menuOpacity.DropDownItems.Add(item);
            }
            contextMenu.Items.Add(menuOpacity);

            // Mouse Range Preset Submenu
            menuMouseRange = new ToolStripMenuItem("마우스 흔들림 반경 (Mouse Range)");
            var presets = new[]
            {
                new { Label = "아주 얌전하게 (가로 6px / 세로 4px)", X = 6.0f, Y = 4.0f },
                new { Label = "보통 (가로 10px / 세로 6px - 기본값)", X = 10.0f, Y = 6.0f },
                new { Label = "자유롭게 (가로 16px / 세로 10px)", X = 16.0f, Y = 10.0f }
            };

            foreach (var p in presets)
            {
                var preset = p;
                ToolStripMenuItem item = new ToolStripMenuItem(preset.Label);
                item.Checked = (Math.Abs(MaxRangeX - preset.X) < 0.1f && Math.Abs(MaxRangeY - preset.Y) < 0.1f);
                item.Click += (s, e) =>
                {
                    MaxRangeX = preset.X;
                    MaxRangeY = preset.Y;
                    SaveConfig();
                    foreach (ToolStripMenuItem mi in menuMouseRange.DropDownItems)
                        mi.Checked = (mi == item);
                };
                menuMouseRange.DropDownItems.Add(item);
            }

            menuMouseRange.DropDownItems.Add(new ToolStripSeparator());
            ToolStripMenuItem openConfigItem = new ToolStripMenuItem("메모장으로 config.ini 직접 편집...");
            openConfigItem.Click += (s, e) =>
            {
                string path = GetConfigPath();
                if (!File.Exists(path)) SaveConfig();
                Process.Start("notepad.exe", path);
            };
            menuMouseRange.DropDownItems.Add(openConfigItem);
            contextMenu.Items.Add(menuMouseRange);

            // Auto-Switch Return Delay Submenu
            ToolStripMenuItem menuReturnDelay = new ToolStripMenuItem("키보드 복귀 대기 시간");
            float[] delays = new float[] { 1.0f, 1.5f, 2.0f, 3.0f, 4.0f };
            string[] delayLabels = new string[] { "1.0초 (빠름)", "1.5초", "2.0초 (기본값)", "3.0초 (여유로움)", "4.0초" };

            for (int i = 0; i < delays.Length; i++)
            {
                float d = delays[i];
                ToolStripMenuItem item = new ToolStripMenuItem(delayLabels[i]);
                item.Checked = (Math.Abs(AutoSwitchDelay - d) < 0.1f);
                item.Click += (s, e) =>
                {
                    AutoSwitchDelay = d;
                    SaveConfig();
                    foreach (ToolStripMenuItem mi in menuReturnDelay.DropDownItems)
                        mi.Checked = (mi == item);
                };
                menuReturnDelay.DropDownItems.Add(item);
            }
            contextMenu.Items.Add(menuReturnDelay);

            contextMenu.Items.Add(new ToolStripSeparator());

            // Always on Top
            menuAlwaysOnTop = new ToolStripMenuItem("항상 위에 유지 (Always on Top)");
            menuAlwaysOnTop.Checked = this.TopMost;
            menuAlwaysOnTop.Click += (s, e) =>
            {
                isAlwaysOnTop = !isAlwaysOnTop;
                this.TopMost = isAlwaysOnTop;
                menuAlwaysOnTop.Checked = isAlwaysOnTop;
            };
            contextMenu.Items.Add(menuAlwaysOnTop);

            // Reset Position
            ToolStripMenuItem menuReset = new ToolStripMenuItem("위치 초기화 (우하단 이동)");
            menuReset.Click += (s, e) =>
            {
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                this.Left = wa.Right - this.Width - 30;
                this.Top = wa.Bottom - this.Height - 30;
                RenderLayers();
            };
            contextMenu.Items.Add(menuReset);

            contextMenu.Items.Add(new ToolStripSeparator());

            // Exit
            ToolStripMenuItem menuExit = new ToolStripMenuItem("종료 (Exit)");
            menuExit.Click += (s, e) =>
            {
                Log("Exit clicked");
                Application.Exit();
            };
            contextMenu.Items.Add(menuExit);

            this.ContextMenuStrip = contextMenu;
        }

        private void SetupTrayIcon()
        {
            trayIcon = new NotifyIcon();
            trayIcon.Text = "CharacterType (스마트 자동 전환)";
            trayIcon.Icon = SystemIcons.Application;
            trayIcon.ContextMenuStrip = contextMenu;
            trayIcon.Visible = true;
        }

        private void OnMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                ReleaseCapture();
                SendMessage(this.Handle, WM_NCLBUTTONDOWN, (IntPtr)HT_CAPTION, IntPtr.Zero);
            }
        }

        private void OnMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                contextMenu.Show(Cursor.Position);
            }
        }

        #endregion

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (s_kbdHookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(s_kbdHookId);
                s_kbdHookId = IntPtr.Zero;
            }

            if (s_mouseHookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(s_mouseHookId);
                s_mouseHookId = IntPtr.Zero;
            }

            if (mouseTrackingTimer != null) mouseTrackingTimer.Dispose();
            if (leftBounceTimer != null) leftBounceTimer.Dispose();
            if (rightBounceTimer != null) rightBounceTimer.Dispose();
            if (trayIcon != null) trayIcon.Dispose();

            if (rawBody != null) rawBody.Dispose();
            if (rawLeftArmReady != null) rawLeftArmReady.Dispose();
            if (rawLeftArmType != null) rawLeftArmType.Dispose();
            if (rawRightArmReady != null) rawRightArmReady.Dispose();
            if (rawRightArmType != null) rawRightArmType.Dispose();
            if (rawMouseWithArm != null) rawMouseWithArm.Dispose();

            if (scaledBody != null) scaledBody.Dispose();
            if (scaledLeftArmReady != null) scaledLeftArmReady.Dispose();
            if (scaledLeftArmType != null) scaledLeftArmType.Dispose();
            if (scaledRightArmReady != null) scaledRightArmReady.Dispose();
            if (scaledRightArmType != null) scaledRightArmType.Dispose();
            if (scaledMouseWithArm != null) scaledMouseWithArm.Dispose();

            if (compositeBitmap != null) compositeBitmap.Dispose();
            if (compositeGraphics != null) compositeGraphics.Dispose();

            base.OnFormClosing(e);
        }

        public static void Log(string message)
        {
            try
            {
                string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log.txt");
                File.AppendAllText(logPath, string.Format("[{0:yyyy-MM-dd HH:mm:ss.fff}] {1}\r\n", DateTime.Now, message));
            }
            catch { }
        }
    }
}
