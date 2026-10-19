using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using ManagedBass;
using ManagedBass.Asio;
using Wpf.Ui.Controls;

namespace M3URadioPlayer
{
    public class RadioStation
    {
        public string Title { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
    }

    public class AppSettings
    {
        public int OutputModeIndex { get; set; } = 0;
        public string OutputDeviceName { get; set; } = string.Empty;
        public string SelectedPlaylist { get; set; } = string.Empty;
        public double Volume { get; set; } = 1.0;
    }

    public partial class MainWindow : FluentWindow
    {
        private int _streamHandle = 0;
        private int _currentDeviceIndex = -1;
        private SyncProcedure? _metaSyncProc;
        private readonly List<int> _deviceMapping = new();
        private readonly string _appDir = AppDomain.CurrentDomain.BaseDirectory;
        private readonly string _settingsFilePath;
        private bool _isInitializing = true;

        // ==========================================
        // Win32 タスクトレイ常駐用 定義
        // ==========================================
        private const int WM_USER = 0x0400;
        private const int WM_TRAYICON = WM_USER + 1;
        private const int WM_LBUTTONDBLCLK = 0x0203;
        private const int WM_RBUTTONUP = 0x0205;

        private const int NIM_ADD = 0x00000000;
        private const int NIM_MODIFY = 0x00000001;
        private const int NIM_DELETE = 0x00000002;
        private const int NIF_MESSAGE = 0x00000001;
        private const int NIF_ICON = 0x00000002;
        private const int NIF_TIP = 0x00000004;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uID;
            public int uFlags;
            public int uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public int dwState;
            public int dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public int uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public int dwInfoFlags;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpdata);

        [DllImport("user32.dll")]
        private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr LoadImage(IntPtr hinst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);
        
        private IntPtr _trayIconHandle = IntPtr.Zero;
        private bool _isTrayActive = false;
        private HwndSource? _hwndSource;

        public MainWindow()
        {
            InitializeComponent();
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            _settingsFilePath = Path.Combine(_appDir, "settings.json");

            InitAudioSystem();
            ScanPlaylists();

            // 設定読み込み＆適用
            LoadSettings();
            _isInitializing = false;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var handle = new WindowInteropHelper(this).Handle;
            _hwndSource = HwndSource.FromHwnd(handle);
            _hwndSource?.AddHook(HwndHook);

            string iconPath = Path.Combine(_appDir, "M3U Radio Player.ico");
            if (File.Exists(iconPath))
            {
                // 1. タスクトレイ用アイコンを読み込み (16x16)
                _trayIconHandle = LoadImage(IntPtr.Zero, iconPath, 1, 16, 16, 0x0010);

                // 2. ウィンドウ左上・タスクバー用アイコンを C# 側から安全にセット
                try
                {
                    this.Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri(iconPath, UriKind.Absolute));
                }
                catch { }
            }

            // アイコンが見つからない場合のフォールバック
            if (_trayIconHandle == IntPtr.Zero)
            {
                _trayIconHandle = LoadIcon(IntPtr.Zero, (IntPtr)32512); // IDI_APPLICATION
            }
        }
        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_TRAYICON)
            {
                int mouseMsg = lParam.ToInt32();
                if (mouseMsg == WM_LBUTTONDBLCLK)
                {
                    RestoreFromTray();
                    handled = true;
                }
                else if (mouseMsg == WM_RBUTTONUP)
                {
                    ShowTrayMenu();
                    handled = true;
                }
            }
            return IntPtr.Zero;
        }

        private void ShowTrayIcon(bool show)
        {
            var handle = new WindowInteropHelper(this).Handle;
            var data = new NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATA)),
                hWnd = handle,
                uID = 1001,
                uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
                uCallbackMessage = WM_TRAYICON,
                hIcon = _trayIconHandle,
                szTip = "M3U Radio Player"
            };

            if (show && !_isTrayActive)
            {
                Shell_NotifyIcon(NIM_ADD, ref data);
                _isTrayActive = true;
            }
            else if (!show && _isTrayActive)
            {
                Shell_NotifyIcon(NIM_DELETE, ref data);
                _isTrayActive = false;
            }
        }

        private void ShowTrayMenu()
        {
            var menu = new System.Windows.Controls.ContextMenu();

            var itemShow = new System.Windows.Controls.MenuItem { Header = "プレイヤーを表示" };
            itemShow.Click += (s, e) => RestoreFromTray();
            menu.Items.Add(itemShow);

            menu.Items.Add(new System.Windows.Controls.Separator());

            var itemExit = new System.Windows.Controls.MenuItem { Header = "終了" };
            itemExit.Click += (s, e) =>
            {
                ShowTrayIcon(false);
                System.Windows.Application.Current.Shutdown();
            };
            menu.Items.Add(itemExit);

            menu.IsOpen = true;
        }

        private void RestoreFromTray()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
            ShowTrayIcon(false);
        }

        private void FluentWindow_StateChanged(object sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized)
            {
                Hide();
                ShowTrayIcon(true);
            }
        }

        private void InitAudioSystem()
        {
            Bass.Configure(Configuration.NetPlaylist, 1);
            Bass.Configure(Configuration.GlobalStreamVolume, 10000);

            string flacPath = Path.Combine(_appDir, "bassflac.dll");
            if (File.Exists(flacPath))
            {
                Bass.PluginLoad(flacPath);
            }
        }

        private void UpdateDeviceList(string? preferredDevice = null)
        {
            CmbDevice.Items.Clear();
            _deviceMapping.Clear();

            bool isAsio = CmbOutputMode.SelectedIndex == 1;

            if (isAsio)
            {
                for (int i = 0; BassAsio.GetDeviceInfo(i, out var info); i++)
                {
                    CmbDevice.Items.Add(info.Name);
                    _deviceMapping.Add(i);
                }
            }
            else
            {
                for (int i = 1; Bass.GetDeviceInfo(i, out var info); i++)
                {
                    if (info.IsEnabled)
                    {
                        CmbDevice.Items.Add(info.Name);
                        _deviceMapping.Add(i);
                    }
                }
            }

            if (CmbDevice.Items.Count > 0)
            {
                int targetIdx = 0;

                if (!string.IsNullOrEmpty(preferredDevice))
                {
                    for (int i = 0; i < CmbDevice.Items.Count; i++)
                    {
                        if (string.Equals(CmbDevice.Items[i]?.ToString(), preferredDevice, StringComparison.OrdinalIgnoreCase))
                        {
                            targetIdx = i;
                            break;
                        }
                    }
                }
                else
                {
                    for (int i = 0; i < CmbDevice.Items.Count; i++)
                    {
                        if (CmbDevice.Items[i] is string devName)
                        {
                            string lower = devName.ToLower();
                            if (lower.Contains("digital output") || lower.Contains("spdif") || lower.Contains("realtek"))
                            {
                                targetIdx = i;
                                break;
                            }
                        }
                    }
                }

                CmbDevice.SelectedIndex = targetIdx;
            }
        }

        private void ScanPlaylists()
        {
            CmbPlaylist.Items.Clear();
            var files = Directory.GetFiles(_appDir, "*.m3u");
            foreach (var f in files)
            {
                CmbPlaylist.Items.Add(Path.GetFileName(f));
            }

            if (CmbPlaylist.Items.Count > 0 && CmbPlaylist.SelectedIndex < 0)
            {
                CmbPlaylist.SelectedIndex = 0;
            }
        }

        private void LoadM3U(string filename)
        {
            ListStations.Items.Clear();
            string path = Path.Combine(_appDir, filename);
            if (!File.Exists(path)) return;

            string currentTitle = "Unknown Station";
            foreach (var line in File.ReadAllLines(path))
            {
                string trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;

                if (trimmed.StartsWith("#EXTINF:"))
                {
                    var parts = trimmed.Split(',', 2);
                    if (parts.Length > 1) currentTitle = parts[1].Trim();
                }
                else if (!trimmed.StartsWith("#"))
                {
                    ListStations.Items.Add(new RadioStation { Title = currentTitle, Url = trimmed });
                    currentTitle = "Unknown Station";
                }
            }

            if (ListStations.Items.Count > 0)
            {
                ListStations.SelectedIndex = 0;
            }
        }

        private async void PlaySelected()
        {
            if (ListStations.SelectedItem is not RadioStation station) return;

            Stop();
            TxtStationName.Text = $"接続中: {station.Title} ...";

            bool isAsio = CmbOutputMode.SelectedIndex == 1;
            int devIdx = CmbDevice.SelectedIndex >= 0 ? _deviceMapping[CmbDevice.SelectedIndex] : -1;
            double currentVolume = SliderVolume.Value;
            string targetUrl = station.Url;
            string stationTitle = station.Title;

            await Task.Run(() =>
            {
                try
                {
                    if (!isAsio)
                    {
                        if (_currentDeviceIndex != devIdx)
                        {
                            Bass.Free();
                            if (!Bass.Init(devIdx, 44100) && Bass.LastError != Errors.Already)
                            {
                                Dispatcher.Invoke(() => TxtStationName.Text = $"デバイス初期化失敗: {Bass.LastError}");
                                return;
                            }
                            _currentDeviceIndex = devIdx;
                        }
                        Bass.CurrentDevice = devIdx;
                    }
                    else
                    {
                        if (_currentDeviceIndex != 0)
                        {
                            Bass.Free();
                            if (!Bass.Init(0, 44100) && Bass.LastError != Errors.Already)
                            {
                                Dispatcher.Invoke(() => TxtStationName.Text = $"BASSコア初期化失敗: {Bass.LastError}");
                                return;
                            }
                            _currentDeviceIndex = 0;
                        }
                        Bass.CurrentDevice = 0;
                    }

                    var flags = isAsio ? BassFlags.Decode : BassFlags.AutoFree;
                    int handle = Bass.CreateStream(targetUrl, 0, flags, null, IntPtr.Zero);

                    if (handle == 0)
                    {
                        var err = Bass.LastError;
                        Dispatcher.Invoke(() => TxtStationName.Text = $"接続失敗 (Error: {err})");
                        return;
                    }

                    _streamHandle = handle;
                    Bass.ChannelSetAttribute(_streamHandle, ChannelAttribute.Volume, currentVolume);
                    SetupMetaSync(handle);
                    Bass.ChannelGetInfo(handle, out var info);

                    bool success = false;
                    if (isAsio)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            BassAsio.Free();
                            if (BassAsio.Init(devIdx, AsioInitFlags.None))
                            {
                                BassAsio.Rate = info.Frequency;
                                BassAsio.ChannelReset(false, -1, AsioChannelResetFlags.Enable | AsioChannelResetFlags.Join | AsioChannelResetFlags.Pause | AsioChannelResetFlags.Format | AsioChannelResetFlags.Rate);
                                BassAsio.ChannelEnableBass(false, 0, handle, true);
                                success = BassAsio.Start();
                            }
                            else
                            {
                                var asioErr = BassAsio.LastError;
                                TxtStationName.Text = $"ASIO初期化失敗 (Error: {asioErr})";
                            }
                        });

                        if (!success) return;
                    }
                    else
                    {
                        success = Bass.ChannelPlay(handle);
                    }

                    Dispatcher.Invoke(() =>
                    {
                        if (success)
                        {
                            TxtStationName.Text = $"再生中: {stationTitle}";
                            TxtStreamInfo.Text = $"Ch: {info.Channels}ch | サンプリングレート: {info.Frequency} Hz";
                            UpdateMetadata(handle);
                        }
                        else
                        {
                            TxtStationName.Text = "出力デバイスの起動に失敗しました";
                        }
                    });
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => TxtStationName.Text = $"エラー: {ex.Message}");
                }
            });
        }

        private void Stop()
        {
            if (_streamHandle != 0)
            {
                if (CmbOutputMode.SelectedIndex == 1)
                {
                    BassAsio.Stop();
                }
                Bass.ChannelStop(_streamHandle);
                Bass.StreamFree(_streamHandle);
                _streamHandle = 0;
            }

            TxtStationName.Text = "停止中";
            TxtTrackTitle.Text = "タイトル: ---";
            TxtStreamInfo.Text = "形式: --- | サンプリングレート: ---";
        }

        private void SetupMetaSync(int handle)
        {
            _metaSyncProc = (syncHandle, channel, data, user) =>
            {
                Dispatcher.Invoke(() => UpdateMetadata(channel));
            };
            Bass.ChannelSetSync(handle, SyncFlags.MetadataReceived, 0, _metaSyncProc);
            Bass.ChannelSetSync(handle, SyncFlags.OggChange, 0, _metaSyncProc);
        }

        private void UpdateMetadata(int handle)
        {
            IntPtr metaPtr = Bass.ChannelGetTags(handle, TagType.META);
            if (metaPtr != IntPtr.Zero)
            {
                string? meta = Marshal.PtrToStringUTF8(metaPtr);
                if (string.IsNullOrEmpty(meta))
                {
                    meta = Marshal.PtrToStringAnsi(metaPtr);
                }

                if (!string.IsNullOrEmpty(meta) && meta.Contains("StreamTitle='"))
                {
                    string track = meta.Split("StreamTitle='")[1].Split("';")[0];
                    TxtTrackTitle.Text = $"タイトル: {WebUtility.HtmlDecode(track.Trim())}";
                    return;
                }
            }

            IntPtr oggTags = Bass.ChannelGetTags(handle, TagType.OGG);
            if (oggTags != IntPtr.Zero)
            {
                string artist = "";
                string title = "";
                IntPtr currentPtr = oggTags;

                while (true)
                {
                    string? entry = Marshal.PtrToStringUTF8(currentPtr);
                    if (string.IsNullOrEmpty(entry)) break;

                    if (entry.StartsWith("ARTIST=", StringComparison.OrdinalIgnoreCase))
                        artist = entry.Substring(7);
                    else if (entry.StartsWith("TITLE=", StringComparison.OrdinalIgnoreCase))
                        title = entry.Substring(6);

                    currentPtr += Encoding.UTF8.GetByteCount(entry) + 1;
                }

                artist = WebUtility.HtmlDecode(artist.Trim());
                title = WebUtility.HtmlDecode(title.Trim());

                if (!string.IsNullOrEmpty(artist) && !string.IsNullOrEmpty(title))
                    TxtTrackTitle.Text = $"タイトル: {artist} - {title}";
                else if (!string.IsNullOrEmpty(title))
                    TxtTrackTitle.Text = $"タイトル: {title}";
            }
        }

        private void SaveSettings()
        {
            if (_isInitializing) return;

            try
            {
                var settings = new AppSettings
                {
                    OutputModeIndex = CmbOutputMode.SelectedIndex,
                    OutputDeviceName = CmbDevice.SelectedItem?.ToString() ?? string.Empty,
                    SelectedPlaylist = CmbPlaylist.SelectedItem?.ToString() ?? string.Empty,
                    Volume = SliderVolume.Value
                };

                string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_settingsFilePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"設定保存失敗: {ex.Message}");
            }
        }

        private void LoadSettings()
        {
            try
            {
                if (File.Exists(_settingsFilePath))
                {
                    string json = File.ReadAllText(_settingsFilePath);
                    var settings = JsonSerializer.Deserialize<AppSettings>(json);
                    if (settings != null)
                    {
                        SliderVolume.Value = settings.Volume;
                        if (TxtVolume != null)
                        {
                            TxtVolume.Text = $"{(int)(settings.Volume * 100)}%";
                        }

                        if (!string.IsNullOrEmpty(settings.SelectedPlaylist) && CmbPlaylist.Items.Contains(settings.SelectedPlaylist))
                        {
                            CmbPlaylist.SelectedItem = settings.SelectedPlaylist;
                        }

                        if (settings.OutputModeIndex >= 0 && settings.OutputModeIndex < CmbOutputMode.Items.Count)
                        {
                            CmbOutputMode.SelectedIndex = settings.OutputModeIndex;
                        }

                        UpdateDeviceList(settings.OutputDeviceName);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"設定読込失敗: {ex.Message}");
            }

            UpdateDeviceList();
        }

        private void BtnPlay_Click(object sender, RoutedEventArgs e) => PlaySelected();
        private void BtnStop_Click(object sender, RoutedEventArgs e) => Stop();
        private void ListStations_MouseDoubleClick(object sender, MouseButtonEventArgs e) => PlaySelected();

        private void SliderVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtVolume != null)
            {
                TxtVolume.Text = $"{(int)(SliderVolume.Value * 100)}%";
            }
            if (_streamHandle != 0)
            {
                Bass.ChannelSetAttribute(_streamHandle, ChannelAttribute.Volume, SliderVolume.Value);
            }
            SaveSettings();
        }

        private void CmbOutputMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isInitializing && IsLoaded)
            {
                UpdateDeviceList();
                SaveSettings();
            }
        }

        private void CmbDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isInitializing && IsLoaded)
            {
                SaveSettings();
                if (_streamHandle != 0) PlaySelected();
            }
        }

        private void CmbPlaylist_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbPlaylist.SelectedItem is string filename)
            {
                LoadM3U(filename);
                SaveSettings();
            }
        }

        private void BtnReload_Click(object sender, RoutedEventArgs e) => ScanPlaylists();

        protected override void OnClosed(EventArgs e)
        {
            SaveSettings();
            ShowTrayIcon(false);

            if (_trayIconHandle != IntPtr.Zero)
            {
                DestroyIcon(_trayIconHandle);
                _trayIconHandle = IntPtr.Zero;
            }

            Stop();
            Bass.Free();
            base.OnClosed(e);
        }
    }
}