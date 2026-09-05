using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using PocketBridge.Core;
using QRCoder;

namespace PocketBridge.Windows;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<ReceiptRow> _receipts = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly bool _preview;
    private readonly string _settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PocketBridge", "settings.json");
    private ReceiverClient? _client;
    private string _destination;
    private bool _busy;
    private bool _closing;
    private bool _allowClose;
    private long _totalReceived;

    public FrameworkElement PreviewSurface => RootView;

    public MainWindow(bool preview = false)
    {
        _preview = preview;
        InitializeComponent();
        _destination = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "PocketBridge");
        ReceiptList.ItemsSource = _receipts;
        if (!preview) LoadSettings();
        UpdateDestinationLabel();
        Closing += Window_Closing;
    }

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return;
            var settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(_settingsPath));
            if (settings is null) return;
            RelayUrlBox.Text = settings.RelayUrl ?? "";
            if (!string.IsNullOrWhiteSpace(settings.Destination)) _destination = Path.GetFullPath(settings.Destination);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            ConnectionMessage.Text = "저장된 설정을 읽지 못했습니다. 서버와 저장 위치를 다시 선택하세요.";
        }
    }

    private void SaveSettings()
    {
        if (_preview) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(new UserSettings(RelayUrlBox.Text.Trim(), _destination)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "설정을 저장하지 못했습니다. 다음 실행에서 다시 입력해야 합니다.\n\n" + e.Message, "PocketBridge", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void UpdateDestinationLabel()
    {
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        DestinationText.Text = _destination.StartsWith(pictures + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? "내 사진" + _destination[pictures.Length..]
            : _destination;
        DestinationText.ToolTip = _destination;
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _closing) return;
        if (string.IsNullOrWhiteSpace(RelayUrlBox.Text))
        {
            SetStatus("설정 필요", "#C18239", "#FFF3DF");
            ConnectionMessage.Text = "중계 서버의 HTTPS 주소가 필요합니다. 시작 가이드에서 준비 방법을 확인하세요.";
            RelayUrlBox.Focus();
            return;
        }

        _busy = true;
        SetInputsEnabled(false);
        StartButtonText.Text = "연결 준비 중…";
        SetStatus("연결 중", "#7967D5", "#EEEBFC");
        ConnectionMessage.Text = "중계 서버에 연결하고 있습니다…";
        ResetPairingVisual();
        try
        {
            await DisposeClientAsync();
            Directory.CreateDirectory(_destination);
            var client = new ReceiverClient(RelayUrlBox.Text.Trim(), _destination);
            _client = client;
            client.Updated += update => DispatchFor(client, () => ApplyUpdate(update));
            client.FileReceived += file => DispatchFor(client, () => AddReceipt(file));
            await client.StartAsync(_lifetime.Token);
            if (_closing || !ReferenceEquals(_client, client)) return;
            SaveSettings();
            if (!client.Completion.IsCompleted && client.Invite is { } invite)
            {
                using var generator = new QRCodeGenerator();
                using var data = generator.CreateQrCode(invite.ToJson(), QRCodeGenerator.ECCLevel.M);
                using var png = new PngByteQRCode(data);
                using var stream = new MemoryStream(png.GetGraphic(8));
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                QrImage.Source = image;
                QrCaption.Text = $"{new Uri(invite.Server).Authority} · 10분 내 스캔\niPhone의 PocketBridge 앱에서 연결하세요.";
                QrPlaceholder.Visibility = Visibility.Collapsed;
                QrPanel.Visibility = Visibility.Visible;
                CopyInviteButton.IsEnabled = true;
                DisconnectButton.Visibility = Visibility.Visible;
                StartButtonText.Text = "iPhone 연결 대기 중";
            }
        }
        catch (OperationCanceledException)
        {
            if (!_closing)
            {
                SetStatus("연결 실패", "#B26162", "#FCECEE");
                ConnectionMessage.Text = "연결 시간이 초과되었습니다. 인터넷과 서버 주소를 확인하고 다시 시도하세요.";
                await DisposeClientAsync();
                EnableNewConnection();
            }
        }
        catch (Exception ex)
        {
            if (!_closing)
            {
                SetStatus("연결 실패", "#B26162", "#FCECEE");
                ConnectionMessage.Text = FriendlyError(ex);
                await DisposeClientAsync();
                EnableNewConnection();
            }
        }
        finally
        {
            _busy = false;
            if (_client is null && !_closing) SetInputsEnabled(true);
        }
    }

    private void DispatchFor(ReceiverClient client, Action action)
    {
        if (Dispatcher.HasShutdownStarted || _closing) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (!_closing && ReferenceEquals(client, _client)) action();
        });
    }

    private void ApplyUpdate(ReceiverUpdate update)
    {
        ConnectionMessage.Text = update.FileName is { Length: > 0 }
            ? $"{update.FileName}\n{update.Message}"
            : update.Message;
        ConnectionMessage.ToolTip = ConnectionMessage.Text;
        switch (update.State)
        {
            case "waiting":
                SetStatus("연결 대기", "#7967D5", "#EEEBFC");
                break;
            case "receiving":
            case "verifying":
                SetStatus(update.State == "verifying" ? "파일 확인 중" : "받는 중", "#5687CA", "#EAF2FF");
                ShowConnected();
                TransferProgress.Visibility = Visibility.Visible;
                TransferProgress.IsIndeterminate = update.State == "verifying";
                TransferProgress.Value = update.Percent;
                ProgressText.Visibility = Visibility.Visible;
                ProgressText.Text = $"{Formatters.Bytes(update.BytesReceived)} / {Formatters.Bytes(update.TotalBytes)}  ·  {update.Percent:0}%";
                StartButtonText.Text = "전송 연결됨";
                break;
            case "received":
                SetStatus("연결됨", "#3A977A", "#E8F6EF");
                ShowConnected();
                TransferProgress.IsIndeterminate = false;
                TransferProgress.Value = 100;
                ProgressText.Text = "검증 완료 · PC에 저장됨";
                break;
            case "disconnected":
            case "error":
                SetStatus(update.State == "error" ? "전송 오류" : "연결 종료", update.State == "error" ? "#B26162" : "#7F8AA1", update.State == "error" ? "#FCECEE" : "#EBEDF4");
                EnableNewConnection();
                break;
        }
    }

    private void ShowConnected()
    {
        QrPanel.Visibility = Visibility.Collapsed;
        QrPlaceholder.Visibility = Visibility.Collapsed;
        ConnectedPanel.Visibility = Visibility.Visible;
        QrImage.Source = null;
        CopyInviteButton.IsEnabled = false;
    }

    private void AddReceipt(ReceivedFile file)
    {
        _receipts.Insert(0, new ReceiptRow(file));
        _totalReceived += file.Size;
        EmptyReceipts.Visibility = Visibility.Collapsed;
        ReceiptList.Visibility = Visibility.Visible;
        ReceiptCountText.Text = $"{_receipts.Count}개 · {Formatters.Bytes(_totalReceived)}";
    }

    private void SetStatus(string text, string foreground, string background)
    {
        StatusBadgeText.Text = text;
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(foreground));
        StatusBadgeText.Foreground = brush;
        StatusDot.Fill = brush;
        StatusBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(background));
    }

    private void SetInputsEnabled(bool enabled)
    {
        RelayUrlBox.IsEnabled = enabled;
        ChooseFolderButton.IsEnabled = enabled;
        StartButton.IsEnabled = enabled;
    }

    private void EnableNewConnection()
    {
        ResetPairingVisual();
        SetInputsEnabled(true);
        StartButtonText.Text = "새 연결 QR 만들기";
        DisconnectButton.Visibility = Visibility.Collapsed;
    }

    private void ResetPairingVisual()
    {
        QrImage.Source = null;
        QrPanel.Visibility = Visibility.Collapsed;
        ConnectedPanel.Visibility = Visibility.Collapsed;
        QrPlaceholder.Visibility = Visibility.Visible;
        CopyInviteButton.IsEnabled = false;
        TransferProgress.IsIndeterminate = false;
        TransferProgress.Visibility = Visibility.Collapsed;
        ProgressText.Visibility = Visibility.Collapsed;
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _closing) return;
        _busy = true;
        DisconnectButton.IsEnabled = false;
        try
        {
            await DisposeClientAsync();
            EnableNewConnection();
            SetStatus("연결 종료", "#7F8AA1", "#EBEDF4");
            ConnectionMessage.Text = "연결을 종료했습니다. 완료된 파일은 보관되며, 미완료 파일은 정리되었습니다.";
        }
        catch (Exception ex) { ConnectionMessage.Text = FriendlyError(ex); EnableNewConnection(); }
        finally { _busy = false; DisconnectButton.IsEnabled = true; }
    }

    private async Task DisposeClientAsync()
    {
        var client = _client;
        _client = null;
        if (client is not null) await client.DisposeAsync();
    }

    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "받은 파일을 저장할 폴더", Multiselect = false };
        if (Directory.Exists(_destination)) dialog.InitialDirectory = _destination;
        if (dialog.ShowDialog(this) != true) return;
        _destination = dialog.FolderName;
        UpdateDestinationLabel();
        SaveSettings();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(_destination);
    private void ShowReceivedFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path }) OpenFolder(Path.GetDirectoryName(path)!);
    }

    private void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(Path.GetFullPath(folder)) { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(this, "폴더를 열지 못했습니다.\n\n" + ex.Message, "PocketBridge", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void CopyInvite_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_client?.Invite is not { } invite) return;
            Clipboard.SetText(invite.ToJson());
            ConnectionMessage.Text = "연결 정보를 복사했습니다. iPhone 앱의 ‘연결 정보 붙여넣기’에 넣으세요. 연결 정보는 다른 사람에게 공유하지 마세요.";
        }
        catch (Exception ex) { MessageBox.Show(this, "클립보드를 사용할 수 없습니다. QR로 연결하거나 잠시 후 다시 시도하세요.\n\n" + ex.Message, "PocketBridge", MessageBoxButton.OK, MessageBoxImage.Information); }
    }

    private void Help_Click(object sender, RoutedEventArgs e)
    {
        var help = new HelpWindow { Owner = this };
        help.ShowDialog();
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || _preview) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        IsEnabled = false;
        try
        {
            await _lifetime.CancelAsync();
            await DisposeClientAsync();
        }
        catch { /* Closing must still release the native window after cleanup failures. */ }
        finally
        {
            _lifetime.Dispose();
            _allowClose = true;
            Close();
        }
    }

    private static string FriendlyError(Exception exception) => exception switch
    {
        System.Net.Http.HttpRequestException => "서버에 연결할 수 없습니다. HTTPS 주소, 인터넷 연결, 서버 실행 상태를 확인하세요.",
        System.Net.WebSockets.WebSocketException => "연결 통로를 열지 못했습니다. 서버가 WebSocket 연결을 허용하는지 확인하세요.",
        UnauthorizedAccessException => "이 저장 폴더에 쓸 수 없습니다. 쓰기 권한이 있는 다른 폴더를 선택하세요.",
        _ => exception.Message
    };

    private sealed record UserSettings(string? RelayUrl, string? Destination);

    private sealed class ReceiptRow(ReceivedFile file)
    {
        public string Name => file.Name;
        public string FullPath => file.FullPath;
        public string Detail => file.WireSize < file.Size
            ? $"{Formatters.Bytes(file.Size)} · 전송 {Formatters.Bytes(file.WireSize)} · {file.ReceivedAt.LocalDateTime:HH:mm}"
            : $"{Formatters.Bytes(file.Size)} · {file.ReceivedAt.LocalDateTime:HH:mm}";
    }
}
