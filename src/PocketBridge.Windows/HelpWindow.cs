using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PocketBridge.Windows;

internal sealed class HelpWindow : Window
{
    public HelpWindow()
    {
        Title = "PocketBridge · 시작 가이드";
        Width = 570;
        Height = 610;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(245, 247, 251));
        FontFamily = new FontFamily("Segoe UI, Malgun Gothic");
        var stack = new StackPanel { Margin = new Thickness(30) };
        stack.Children.Add(Label("처음 연결하기", 26, FontWeights.Bold));
        stack.Children.Add(Label("Windows 앱, iPhone 단축어와 HTTPS 중계 서버가 필요해요.", 13, FontWeights.Normal, "#6D788C", new Thickness(0, 10, 0, 22)));
        AddStep(stack, "1", "중계 서버 준비", "이 프로젝트에는 공개 중계 서비스가 포함되어 있지 않습니다. 운영자가 서버를 배포한 뒤 HTTPS 주소를 제공해야 합니다. 서로 다른 Wi-Fi나 iPhone 셀룰러에서도 인터넷으로 연결됩니다.", "docs/relay.md 열기", "relay.md");
        AddStep(stack, "2", "iPhone 단축어 만들기", "iPhone의 단축어 앱에서 제공된 PocketBridge 레시피를 한 번만 만드세요. Mac, Xcode, 유료 개발자 계정이 필요 없습니다.", "docs/shortcut.md 열기", "shortcut.md");
        AddStep(stack, "3", "공유 시트에서 전송", "Windows에서 서버 주소와 저장 폴더를 지정하고 QR을 만드세요. 사진 또는 파일을 선택해 공유 → PocketBridge 단축어 → QR 스캔 순서로 보냅니다.");
        stack.Children.Add(Label("연결이 끊기면 새 QR로 연결하고 미완료 파일을 다시 보내세요.\n다른 앱의 비공개 파일은 iOS에서 선택·공유할 수 있어야 합니다.", 11, FontWeights.Normal, "#6D788C", new Thickness(0, 5, 0, 15)));
        var close = new Button { Content = "알겠어요", HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 100, IsDefault = true, Style = (Style)Application.Current.FindResource("PrimaryButton") };
        close.Click += (_, _) => Close();
        stack.Children.Add(close);
        Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private void AddStep(Panel parent, string number, string title, string body, string? linkLabel = null, string? fileName = null)
    {
        var section = new StackPanel { Margin = new Thickness(0, 0, 0, 19) };
        section.Children.Add(Label($"{number}   {title}", 14, FontWeights.SemiBold));
        section.Children.Add(Label(body, 12, FontWeights.Normal, "#6D788C", new Thickness(20, 7, 0, 0)));
        if (fileName is not null)
        {
            var button = new Button { Content = linkLabel, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(20, 8, 0, 0), FontSize = 10 };
            button.Click += (_, _) => OpenDocument(fileName);
            section.Children.Add(button);
        }
        parent.Children.Add(section);
    }

    private void OpenDocument(string fileName)
    {
        try
        {
            foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            {
                var directory = new DirectoryInfo(start);
                for (var depth = 0; directory is not null && depth < 8; depth++, directory = directory.Parent)
                {
                    var file = Path.Combine(directory.FullName, "docs", fileName);
                    if (!File.Exists(file)) continue;
                    Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
                    return;
                }
            }
            MessageBox.Show(this, $"프로젝트 저장소의 docs/{fileName} 문서를 확인하세요.\n이 실행 파일 주변에서 문서를 찾지 못했습니다. GitHub에서 소스와 문서를 함께 내려받으세요.", "설치 문서", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { MessageBox.Show(this, $"문서를 열지 못했습니다. 프로젝트의 docs/{fileName} 파일을 직접 열어주세요.\n\n{ex.Message}", "설치 문서", MessageBoxButton.OK, MessageBoxImage.Information); }
    }

    private static TextBlock Label(string text, double size, FontWeight weight, string color = "#192336", Thickness? margin = null) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = weight,
        Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)),
        TextWrapping = TextWrapping.Wrap,
        LineHeight = size + 7,
        Margin = margin ?? new Thickness(0)
    };
}
