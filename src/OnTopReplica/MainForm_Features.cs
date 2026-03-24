using OnTopReplica.Native;
using OnTopReplica.Properties;
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Windows.Forms;
using WindowsFormsAero.TaskDialog;

namespace OnTopReplica {
    //Contains some feature implementations of MainForm
    partial class MainForm {

        #region Click forwarding

        public bool ClickForwardingEnabled {
            get {
                return _thumbnailPanel.ReportThumbnailClicks;
            }
            set {
                if (value && Settings.Default.FirstTimeClickForwarding) {
                    TaskDialog dlg = new TaskDialog(Strings.InfoClickForwarding, Strings.InfoClickForwardingTitle, Strings.InfoClickForwardingContent) {
                        CommonButtons = CommonButton.Yes | CommonButton.No
                    };
                    if (dlg.Show(this).CommonButton == CommonButtonResult.No)
                        return;

                    Settings.Default.FirstTimeClickForwarding = false;
                }

                _thumbnailPanel.ReportThumbnailClicks = value;
            }
        }

        #endregion

        #region Click-through

        bool _clickThrough = false;

        readonly Color DefaultNonClickTransparencyKey;

        public bool ClickThroughEnabled {
            get {
                return _clickThrough;
            }
            set {
                TransparencyKey = (value) ? Color.Black : DefaultNonClickTransparencyKey;
                if (value) {
                    //Re-force as top most (always helps in some cases)
                    TopMost = false;
                    this.Activate();
                    TopMost = true;
                }

                _clickThrough = value;
            }
        }

        //Must NOT be equal to any other valid opacity value
        const double ClickThroughHoverOpacity = 0.6;

        Timer _clickThroughComeBackTimer = null;
        long _clickThroughComeBackTicks;
        const int ClickThroughComeBackTimerInterval = 1000;

        /// <summary>
        /// When the mouse hovers over a fully opaque click-through form,
        /// this fades the form to semi-transparency
        /// and starts a timeout to get back to full opacity.
        /// </summary>
        private void RefreshClickThroughComeBack() {
            if (this.Opacity == 1.0) {
                this.Opacity = ClickThroughHoverOpacity;
            }

            if (_clickThroughComeBackTimer == null) {
                _clickThroughComeBackTimer = new Timer();
                _clickThroughComeBackTimer.Tick += _clickThroughComeBackTimer_Tick;
                _clickThroughComeBackTimer.Interval = ClickThroughComeBackTimerInterval;
            }
            _clickThroughComeBackTicks = DateTime.UtcNow.Ticks;
            _clickThroughComeBackTimer.Start();
        }

        void _clickThroughComeBackTimer_Tick(object sender, EventArgs e) {
            var diff = DateTime.UtcNow.Subtract(new DateTime(_clickThroughComeBackTicks));
            if (diff.TotalSeconds > 2) {
                var mousePointer = WindowMethods.GetCursorPos();

                if (!this.ContainsMousePointer(mousePointer)) {
                    if (this.Opacity == ClickThroughHoverOpacity) {
                        this.Opacity = 1.0;
                    }
                    _clickThroughComeBackTimer.Stop();
                }
            }
        }

        #endregion

        #region Chrome

        readonly FormBorderStyle DefaultBorderStyle; // = FormBorderStyle.Sizable; // FormBorderStyle.SizableToolWindow;

        public bool IsChromeVisible {
            get {
                return (FormBorderStyle == DefaultBorderStyle);
            }
            set {
                //Cancel hiding chrome if no thumbnail is shown
                if (!value && !_thumbnailPanel.IsShowingThumbnail)
                    return;

                if (!value) {
                    Location = new Point {
                        X = Location.X + SystemInformation.FrameBorderSize.Width,
                        Y = Location.Y + SystemInformation.FrameBorderSize.Height
                    };
                    FormBorderStyle = FormBorderStyle.None;
                }
                else if(value) {
                    Location = new Point {
                        X = Location.X - SystemInformation.FrameBorderSize.Width,
                        Y = Location.Y - SystemInformation.FrameBorderSize.Height
                    };
                    FormBorderStyle = DefaultBorderStyle;
                }

                Program.Platform.OnFormStateChange(this);
                Invalidate();
            }
        }

        #endregion

        #region DoubleClickRestore

        private bool _isRestoreEnabled = true;
        private IntPtr _restoreTargetHandle = IntPtr.Zero;
        private Timer _restoreWatchTimer;
        private Timer _singleClickTimer;

        public bool IsRestoreEnabled {
            get { return _isRestoreEnabled; }
            set { _isRestoreEnabled = value; }
        }

        /// <summary>
        /// True while the form is hidden and waiting for the user to leave the target window.
        /// OnActivated and OnDeactivate must check this and bail out to avoid fighting with the hidden state.
        /// </summary>
        public bool IsWaitingForRestoreSwitch {
            get { return _restoreTargetHandle != IntPtr.Zero; }
        }

        /// <summary>
        /// Hides OnTopReplica and switches to the target window.
        /// Starts monitoring for the user switching away from the target.
        /// </summary>
        public void PerformDoubleClickRestore() {
            if (CurrentThumbnailWindowHandle == null)
                return;

            _restoreTargetHandle = CurrentThumbnailWindowHandle.Handle;
            Log.Write("DblClickRestore: hiding, target=0x{0:X}, self=0x{1:X}", _restoreTargetHandle.ToInt64(), this.Handle.ToInt64());
            Program.Platform.HideForm(this);
            Log.Write("DblClickRestore: HideForm done, calling SetForegroundWindow");
            Native.InputMethods.AllowSetForegroundWindowHack();
            Native.WindowManagerMethods.SetForegroundWindow(_restoreTargetHandle);
            Log.Write("DblClickRestore: SetForegroundWindow done, starting timer");
            StartRestoreWatchTimer();
        }

        /// <summary>
        /// Called when a foreground window change is detected (via shell hook or polling timer).
        /// Restores OnTopReplica when the user switches away from the target window.
        /// </summary>
        public void HandleRestoreWindowChange(IntPtr activatedWindow) {
            if (_restoreTargetHandle == IntPtr.Zero)
                return;

            // Ignore null foreground (transient state, desktop focus, start menu, etc.)
            if (activatedWindow == IntPtr.Zero)
                return;

            if (activatedWindow == this.Handle) {
                Log.Write("DblClickRestore: ignoring self-activation 0x{0:X}", activatedWindow.ToInt64());
                return;
            }

            if (activatedWindow == _restoreTargetHandle) {
                return; // target still focused, expected
            }

            Log.Write("DblClickRestore: user left target, activated=0x{0:X}, restoring", activatedWindow.ToInt64());
            CompleteRestore();
        }

        private void CompleteRestore() {
            Log.Write("DblClickRestore: CompleteRestore called");
            _restoreTargetHandle = IntPtr.Zero;
            StopRestoreWatchTimer();
            EnsureMainFormVisible();
        }

        /// <summary>
        /// Cancels the restore-watch state without restoring the form.
        /// Called when the form is explicitly restored via tray icon, hotkey, etc.
        /// </summary>
        public void CancelRestoreWatch() {
            _restoreTargetHandle = IntPtr.Zero;
            StopRestoreWatchTimer();
        }

        private void StartRestoreWatchTimer() {
            if (_restoreWatchTimer == null) {
                _restoreWatchTimer = new Timer();
                _restoreWatchTimer.Interval = 500;
                _restoreWatchTimer.Tick += RestoreWatchTimer_Tick;
            }
            _restoreWatchTimer.Start();
        }

        private void StopRestoreWatchTimer() {
            if (_restoreWatchTimer != null)
                _restoreWatchTimer.Stop();
        }

        private void RestoreWatchTimer_Tick(object sender, EventArgs e) {
            if (_restoreTargetHandle == IntPtr.Zero) {
                StopRestoreWatchTimer();
                return;
            }
            IntPtr foreground = Native.WindowManagerMethods.GetForegroundWindow();
            Log.Write("DblClickRestore: timer tick, foreground=0x{0:X}", foreground.ToInt64());
            HandleRestoreWindowChange(foreground);
        }

        /// <summary>
        /// Called on WM_NCLBUTTONDOWN on the caption. Starts a timer to detect single-click
        /// (as opposed to drag or double-click, which cancel the timer).
        /// </summary>
        public void StartSingleClickDetection() {
            if (_singleClickTimer == null) {
                _singleClickTimer = new Timer();
                _singleClickTimer.Interval = SystemInformation.DoubleClickTime + 50;
                _singleClickTimer.Tick += SingleClickTimer_Tick;
            }
            _singleClickTimer.Stop();
            _singleClickTimer.Start();
        }

        /// <summary>
        /// Cancels pending single-click detection (called on drag or double-click).
        /// </summary>
        public void CancelSingleClickDetection() {
            if (_singleClickTimer != null)
                _singleClickTimer.Stop();
        }

        private void SingleClickTimer_Tick(object sender, EventArgs e) {
            _singleClickTimer.Stop();
            if (IsRestoreEnabled) {
                Log.Write("DblClickRestore: single-click detected, triggering restore");
                PerformDoubleClickRestore();
            }
        }

        #endregion

        #region Static content detection

        private Timer _staticDetectTimer;
        private byte[] _previousFrameHash;
        private int _staticSeconds;
        private bool _hasEverMoved;
        private bool _hasAlerted;
        private int _alertSeconds;
        private Timer _slideTimer;
        private Point _slideFrom;
        private Point _slideTo;
        private int _slideStep;
        const int SlideSteps = 60;
        const int SlideIntervalMs = 50; // 60 * 50ms = 3 seconds
        const int StaticDetectIntervalMs = 1000;
        const int StaticThresholdSeconds = 5;

        /// <summary>
        /// Starts monitoring the replica for static content.
        /// Called when a thumbnail is set and the form is visible.
        /// </summary>
        public void StartStaticDetection() {
            if (_staticDetectTimer == null) {
                _staticDetectTimer = new Timer();
                _staticDetectTimer.Interval = StaticDetectIntervalMs;
                _staticDetectTimer.Tick += StaticDetectTimer_Tick;
            }
            ResetStaticDetectionState();
            _staticDetectTimer.Start();
        }

        /// <summary>
        /// Stops monitoring for static content.
        /// </summary>
        public void StopStaticDetection() {
            if (_staticDetectTimer != null)
                _staticDetectTimer.Stop();
            ResetStaticDetectionState();
        }

        private void ResetStaticDetectionState() {
            _previousFrameHash = null;
            _staticSeconds = 0;
            _hasEverMoved = false;
            _alertSeconds = 0;
            _hasAlerted = false;
            if (_slideTimer != null)
                _slideTimer.Stop();
        }

        const int AutoFocusDelaySeconds = 15;

        private void StaticDetectTimer_Tick(object sender, EventArgs e) {
            if (!_thumbnailPanel.IsShowingThumbnail || Program.Platform.IsHidden(this)) {
                return;
            }

            // While alerted (sliding/sitting at center), just count down to auto-focus
            if (_hasAlerted) {
                _alertSeconds++;

                if (_alertSeconds >= AutoFocusDelaySeconds && CurrentThumbnailWindowHandle != null) {
                    Log.Write("StaticDetect: auto-focus target after {0}s", _alertSeconds);
                    _hasAlerted = false;
                    PerformDoubleClickRestore();
                }
                return;
            }

            byte[] currentHash = CaptureReplicaHash();
            if (currentHash == null) {
                return;
            }

            bool isStatic = _previousFrameHash != null && HashesEqual(currentHash, _previousFrameHash);
            _previousFrameHash = currentHash;

            if (isStatic) {
                _staticSeconds++;

                if (_hasEverMoved && _staticSeconds >= StaticThresholdSeconds) {
                    Log.Write("StaticDetect: static {0}s after movement, sliding to center", _staticSeconds);
                    SlideToCenter();
                    _hasAlerted = true;
                    _alertSeconds = 0;
                }
            }
            else {
                _hasEverMoved = true;
                _staticSeconds = 0;
            }
        }

        const int HashThumbnailSize = 32;
        const int ColorQuantize = 4; // reduce each channel to 64 levels (filters sub-pixel noise, keeps real changes)

        private byte[] CaptureReplicaHash() {
            try {
                var panel = _thumbnailPanel;
                var screenBounds = panel.RectangleToScreen(panel.ClientRectangle);
                if (screenBounds.Width <= 0 || screenBounds.Height <= 0)
                    return null;

                // Capture screen, downsample to 8x8, quantize colors, then hash
                using (var fullBmp = new Bitmap(screenBounds.Width, screenBounds.Height, PixelFormat.Format32bppRgb)) {
                    using (var g = Graphics.FromImage(fullBmp)) {
                        g.CopyFromScreen(screenBounds.Location, Point.Empty, screenBounds.Size);
                    }

                    using (var smallBmp = new Bitmap(HashThumbnailSize, HashThumbnailSize, PixelFormat.Format32bppRgb)) {
                        using (var g = Graphics.FromImage(smallBmp)) {
                            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                            g.DrawImage(fullBmp, 0, 0, HashThumbnailSize, HashThumbnailSize);
                        }

                        // Quantize pixels and build a compact fingerprint
                        byte[] fingerprint = new byte[HashThumbnailSize * HashThumbnailSize * 3];
                        int idx = 0;
                        for (int y = 0; y < HashThumbnailSize; y++) {
                            for (int x = 0; x < HashThumbnailSize; x++) {
                                var c = smallBmp.GetPixel(x, y);
                                fingerprint[idx++] = (byte)(c.R / ColorQuantize);
                                fingerprint[idx++] = (byte)(c.G / ColorQuantize);
                                fingerprint[idx++] = (byte)(c.B / ColorQuantize);
                            }
                        }

                        using (var md5 = MD5.Create()) {
                            return md5.ComputeHash(fingerprint);
                        }
                    }
                }
            }
            catch {
                return null;
            }
        }

        private static bool HashesEqual(byte[] a, byte[] b) {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        private void SlideToCenter() {
            var screen = Screen.FromControl(this);
            int cx = screen.WorkingArea.X + (screen.WorkingArea.Width - this.Width) / 2;
            int cy = screen.WorkingArea.Y + (screen.WorkingArea.Height - this.Height) / 2;

            _slideFrom = this.Location;
            _slideTo = new Point(cx, cy);
            _slideStep = 0;

            if (_slideTimer == null) {
                _slideTimer = new Timer();
                _slideTimer.Interval = SlideIntervalMs;
                _slideTimer.Tick += SlideTimer_Tick;
            }
            _slideTimer.Start();
        }

        private void SlideTimer_Tick(object sender, EventArgs e) {
            _slideStep++;
            if (_slideStep >= SlideSteps) {
                _slideTimer.Stop();
                this.Location = _slideTo;
                return;
            }

            // Ease-out: fast start, slow finish
            double t = (double)_slideStep / SlideSteps;
            double ease = 1.0 - (1.0 - t) * (1.0 - t);

            int x = _slideFrom.X + (int)((_slideTo.X - _slideFrom.X) * ease);
            int y = _slideFrom.Y + (int)((_slideTo.Y - _slideFrom.Y) * ease);
            this.Location = new Point(x, y);
        }

        #endregion

        #region Position lock

        ScreenPosition? _positionLock = null;

        /// <summary>
        /// Gets or sets the screen position where the window is currently locked in.
        /// </summary>
        public ScreenPosition? PositionLock {
            get {
                return _positionLock;
            }
            set {
                if (value != null)
                    this.SetScreenPosition(value.Value);

                _positionLock = value;
            }
        }

        /// <summary>
        /// Refreshes window position if in lock mode.
        /// </summary>
        private void RefreshScreenLock() {
            //If locked in position, move accordingly
            if (PositionLock.HasValue) {
                this.SetScreenPosition(PositionLock.Value);
            }
        }

        #endregion

    }
}
