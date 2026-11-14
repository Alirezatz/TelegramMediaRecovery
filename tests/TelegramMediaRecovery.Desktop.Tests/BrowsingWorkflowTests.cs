using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SkiaSharp;
using TelegramMediaRecovery.Core.Media;
using TelegramMediaRecovery.Core.Tests;
using TelegramMediaRecovery.Desktop.ViewModels;
using TelegramMediaRecovery.Desktop.Views;

namespace TelegramMediaRecovery.Desktop.Tests;

public sealed class BrowsingWorkflowTests
{
    [Theory]
    [InlineData("idle")]
    [InlineData("double_close")]
    [InlineData("scan")]
    [InlineData("preview")]
    public async Task WindowClosingDrainsWorkWithoutReenteringTheClosingEvent(string scenario)
    {
        await Wpf.Run(async () =>
        {
            using var fixture = scenario is "scan" or "preview" ? Media() : null;
            using var viewModel = new MainViewModel(Dispatcher.CurrentDispatcher) { SourcePath = fixture?.Source ?? string.Empty };
            var errors = new List<Exception>();
            DispatcherUnhandledExceptionEventHandler handler = (_, e) => { errors.Add(e.Exception); e.Handled = true; };
            Dispatcher.CurrentDispatcher.UnhandledException += handler;
            var window = new MainWindow(viewModel)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000,
                Top = -10000,
                ShowInTaskbar = false,
                ShowActivated = false,
            };
            var closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var closeCount = 0;
            window.Closed += (_, _) => { closeCount++; closed.TrySetResult(true); };
            try
            {
                window.Show();
                var operation = Task.CompletedTask;
                if (scenario == "scan") { operation = viewModel.ScanCommand.ExecuteAsync(); }
                if (scenario == "preview")
                {
                    await viewModel.ScanAsync();
                    viewModel.CurrentItem = viewModel.Items.Single(i => i.Extension == "TGS");
                    await viewModel.CurrentPreview;
                }
                window.Close();
                if (scenario == "double_close") { window.Close(); }
                await operation;
                await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Empty(errors);
                Assert.False(window.IsVisible);
                Assert.Equal(1, closeCount);
                Assert.Null(viewModel.PreviewImage);
                Assert.Null(viewModel.PreviewUri);
            }
            finally
            {
                if (window.IsVisible) { window.Close(); await closed.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
                Dispatcher.CurrentDispatcher.UnhandledException -= handler;
            }
        });
    }
    [Fact]
    public async Task ScanFilterPreviewSelectAndExportUseTheSameCatalog()
    {
        await Wpf.Run(async () =>
        {
            using var fixture = Media();
            using var viewModel = new MainViewModel(Dispatcher.CurrentDispatcher) { SourcePath = fixture.Source };
            await viewModel.ScanAsync();
            Assert.True(viewModel.HasCatalog);
            Assert.Equal(5, viewModel.Items.Count);
            Assert.False(Directory.Exists(fixture.Output));
            viewModel.KindFilter = "Images";
            Assert.Equal(2, viewModel.VisibleCount);
            viewModel.SelectVisibleCommand.Execute(null);
            Assert.Equal(2, viewModel.SelectedCount);
            viewModel.KindFilter = "Audio";
            Assert.Equal(2, viewModel.VisibleCount);
            Assert.Contains("2 hidden by filters", viewModel.SelectionSummary);
            viewModel.ClearSelectionCommand.Execute(null);
            Assert.Equal(0, viewModel.SelectedCount);
            Assert.False(viewModel.ExportCommand.CanExecute(null));
            viewModel.ResetFiltersCommand.Execute(null);
            viewModel.Query = "png";
            Assert.Equal(1, viewModel.VisibleCount);
            var item = viewModel.Items.Single(i => i.Extension == "PNG");
            viewModel.CurrentItem = item;
            await viewModel.CurrentPreview;
            Assert.NotNull(viewModel.PreviewImage);
            await viewModel.EnsureThumbnailAsync(item);
            Assert.NotNull(item.Thumbnail);
            viewModel.SelectVisibleCommand.Execute(null);
            await viewModel.ExportSelectedAsync(fixture.Output);
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(viewModel.LastOutput, "manifest.json")));
            Assert.Equal(1, manifest.RootElement.GetProperty("Files").GetArrayLength());
            Assert.False(viewModel.IsBusy);
        });
    }

    [Fact]
    public async Task ExportLocksSelectionAndSwitchingMediaResetsPlaybackControls()
    {
        await Wpf.Run(async () =>
        {
            using var fixture = Media();
            using var viewModel = new MainViewModel(Dispatcher.CurrentDispatcher) { SourcePath = fixture.Source };
            await viewModel.ScanAsync();
            viewModel.Items[0].IsChecked = true;
            var export = viewModel.ExportSelectedAsync(fixture.Output);
            Assert.True(viewModel.IsBusy);
            Assert.False(viewModel.CanEditSource);
            Assert.False(viewModel.ClearSelectionCommand.CanExecute(null));
            Assert.False(viewModel.SelectVisibleCommand.CanExecute(null));
            await export;
            Assert.True(viewModel.ClearSelectionCommand.CanExecute(null));
            var window = new MainWindow(viewModel)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000,
                Top = -10000,
                ShowInTaskbar = false,
                ShowActivated = false,
            };
            var closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Closed += (_, _) => closed.TrySetResult(true);
            try
            {
                window.Show();
                viewModel.CurrentItem = viewModel.Items.First(i => i.Extension == "WAV");
                await viewModel.CurrentPreview;
                var slider = (Slider)window.FindName("Seek");
                slider.Maximum = 100; slider.Value = 1;
                viewModel.CurrentItem = viewModel.Items.Single(i => i.Extension == "PNG");
                await viewModel.CurrentPreview;
                Assert.Equal(0, slider.Value);
                Assert.Equal(1, slider.Maximum);
                Assert.Null(((MediaElement)window.FindName("Player")).Source);
            }
            finally { window.Close(); await closed.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        });
    }

    [Fact]
    public async Task WebpAndLottiePreviewsAreDecodedAndSourceChangesClearSelection()
    {
        await Wpf.Run(async () =>
        {
            using var fixture = Media();
            using var viewModel = new MainViewModel(Dispatcher.CurrentDispatcher) { SourcePath = fixture.Source };
            await viewModel.ScanAsync();
            foreach (var extension in new[] { "WEBP", "TGS" })
            {
                viewModel.CurrentItem = viewModel.Items.Single(i => i.Extension == extension);
                await viewModel.CurrentPreview;
                Assert.NotNull(viewModel.PreviewImage);
                Assert.DoesNotContain("unavailable", viewModel.PreviewMessage);
            }
            viewModel.KindFilter = "Audio";
            viewModel.SelectVisibleCommand.Execute(null);
            viewModel.CurrentItem = viewModel.Items.First(i => i.Extension == "WAV");
            await viewModel.CurrentPreview;
            var preview = viewModel.PreviewUri!.LocalPath;
            Assert.True(File.Exists(preview));
            viewModel.SourcePath = Path.Combine(fixture.Root, "another-source");
            Assert.Empty(viewModel.Items);
            Assert.Equal(0, viewModel.SelectedCount);
            Assert.Null(viewModel.PreviewUri);
            Assert.False(File.Exists(preview));
        });
    }

    [Fact]
    public async Task AnEmptyScanUpdatesTheBoundEmptyStateMessage()
    {
        await Wpf.Run(async () =>
        {
            using var fixture = new RecoveryFixture();
            fixture.Add("cache", new CacheKey(1, 1), new byte[20]);
            fixture.Commit();
            using var viewModel = new MainViewModel(Dispatcher.CurrentDispatcher) { SourcePath = fixture.Source };
            var message = new TextBlock();
            message.SetBinding(TextBlock.TextProperty, new Binding(nameof(MainViewModel.EmptyMessage)) { Source = viewModel });
            Assert.Contains("Choose a data source", message.Text);
            await viewModel.ScanAsync();
            Assert.True(viewModel.HasCatalog);
            Assert.Empty(viewModel.Items);
            Assert.Contains("No readable media found", message.Text);
        });
    }

    [Fact]
    public async Task InvalidFiltersExplainWhyThereAreNoMatchingResults()
    {
        await Wpf.Run(async () =>
        {
            using var fixture = Media();
            using var viewModel = new MainViewModel(Dispatcher.CurrentDispatcher) { SourcePath = fixture.Source };
            await viewModel.ScanAsync();
            viewModel.MinimumSize = "invalid-size";
            Assert.NotEmpty(viewModel.FilterError);
            Assert.Equal(0, viewModel.VisibleCount);
            viewModel.ResetFiltersCommand.Execute(null);
            Assert.Equal(5, viewModel.VisibleCount);
            viewModel.MinimumSize = "2MB";
            viewModel.MaximumSize = "1MB";
            Assert.NotEmpty(viewModel.FilterError);
        });
    }

    [Fact]
    public async Task UsageFiltersSeparateAvatarsAndCoversWithoutDroppingUnknownMedia()
    {
        await Wpf.Run(async () =>
        {
            using var fixture = new RecoveryFixture();
            fixture.Add("cache", new CacheKey(0x780804, 1), Image(SKEncodedImageFormat.Png, false));
            fixture.Add("cache", new CacheKey(0x904, 2), Image(SKEncodedImageFormat.Webp, true));
            fixture.Add("cache", new CacheKey(0x204, 3), Image(SKEncodedImageFormat.Png, true));
            fixture.Add("cache", new CacheKey(0x104, 4), Image(SKEncodedImageFormat.Webp, false), tag: 2);
            fixture.Add("cache", new CacheKey(10, 5), Image(SKEncodedImageFormat.Png, false));
            fixture.Add("media_cache", new CacheKey(0x10000, 0x10000), RecoveryFixture.Wave(11));
            fixture.Commit();
            using var viewModel = new MainViewModel(Dispatcher.CurrentDispatcher) { SourcePath = fixture.Source };
            await viewModel.ScanAsync();
            Assert.Equal(6, viewModel.VisibleCount);
            viewModel.UsageFilter = "Avatars / icons";
            Assert.Equal(1, viewModel.VisibleCount);
            viewModel.CurrentItem = viewModel.Items.Single(i => i.Item.Usage == CacheUsage.ProfilePhoto);
            await viewModel.CurrentPreview;
            Assert.Contains("Avatars", viewModel.CurrentDetails);
            Assert.Contains("cache key", viewModel.CurrentItem.UsageDescription);
            viewModel.SelectVisibleCommand.Execute(null);
            viewModel.UsageFilter = "Hide avatars and thumbnails";
            Assert.Equal(4, viewModel.VisibleCount);
            Assert.Contains("1 hidden by filters", viewModel.SelectionSummary);
            viewModel.ClearSelectionCommand.Execute(null);
            viewModel.UsageFilter = "Media files";
            Assert.Equal(2, viewModel.VisibleCount);
            viewModel.KindFilter = "Images";
            Assert.Equal(1, viewModel.VisibleCount);
            viewModel.UsageFilter = "Stickers / emoji";
            Assert.Equal(1, viewModel.VisibleCount); // Static WebP remains a sticker, even though its kind is Image.
            viewModel.UsageFilter = "Unknown usage";
            Assert.Equal(1, viewModel.VisibleCount);
            viewModel.SelectVisibleCommand.Execute(null);
            await viewModel.ExportSelectedAsync(fixture.Output);
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(viewModel.LastOutput, "manifest.json")));
            var file = Assert.Single(manifest.RootElement.GetProperty("Files").EnumerateArray());
            Assert.Equal("Unknown", file.GetProperty("CacheUsage").GetString());
            viewModel.ResetFiltersCommand.Execute(null);
            Assert.Equal("All usage", viewModel.UsageFilter);
            Assert.Equal(6, viewModel.VisibleCount);
        });
    }

    [Fact]
    public async Task AsyncCommandsCannotStartTheSameOperationTwice()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var command = new AsyncRelayCommand(async () => { calls++; await ready.Task; });
        var first = command.ExecuteAsync();
        Assert.False(command.CanExecute(null));
        await command.ExecuteAsync();
        Assert.Equal(1, calls);
        ready.SetResult();
        await first;
        Assert.True(command.CanExecute(null));
    }

    [Fact]
    public async Task AnimationReusesItsSurfaceAndSwitchingSourceDrainsPreviewWork()
    {
        await Wpf.Run(async () =>
        {
            using var fixture = Media();
            using var viewModel = new MainViewModel(Dispatcher.CurrentDispatcher) { SourcePath = fixture.Source };
            await viewModel.ScanAsync();
            viewModel.CurrentItem = viewModel.Items.Single(i => i.Extension == "TGS");
            await viewModel.CurrentPreview;
            var surface = Assert.IsType<WriteableBitmap>(viewModel.PreviewImage);
            await Task.Delay(120);
            Assert.Same(surface, viewModel.PreviewImage);
            var requests = new List<Task>();
            foreach (var item in viewModel.Items)
            {
                viewModel.CurrentItem = item;
                requests.Add(viewModel.CurrentPreview);
            }
            viewModel.SourcePath = string.Empty;
            await Task.WhenAll(requests);
            await viewModel.CancelAndWaitAsync();
            Assert.Null(viewModel.PreviewImage);
            Assert.Null(viewModel.PreviewUri);
            Assert.Empty(viewModel.Items);
        });
    }

    [Fact]
    public async Task OffscreenThumbnailRequestsCanBeCancelledAndPngOutputIsBounded()
    {
        await Wpf.Run(async () =>
        {
            using var fixture = Media();
            using var viewModel = new MainViewModel(Dispatcher.CurrentDispatcher) { SourcePath = fixture.Source };
            await viewModel.ScanAsync();
            var image = viewModel.Items.Single(i => i.Extension == "PNG");
            var discarded = viewModel.EnsureThumbnailAsync(image);
            viewModel.ReleaseThumbnail(image);
            await discarded;
            Assert.Null(image.Thumbnail);
            await viewModel.EnsureThumbnailAsync(image);
            Assert.NotNull(image.Thumbnail);
            Assert.InRange(image.Thumbnail.PixelWidth, 1, 104);
            Assert.InRange(image.Thumbnail.PixelHeight, 1, 104);
            await viewModel.CancelAndWaitAsync();
        });
    }

    [Fact]
    public async Task RapidScrollingKeepsTheThumbnailQueueBoundedAndFinishesAllWaiters()
    {
        await Wpf.Run(async () =>
        {
            using var fixture = new RecoveryFixture();
            var bytes = Image(SKEncodedImageFormat.Png, false);
            for (var n = 1; n <= 80; n++) { fixture.Add("cache", new CacheKey(0x780804, (ulong)n), bytes); }
            fixture.Commit();
            using var viewModel = new MainViewModel(Dispatcher.CurrentDispatcher) { SourcePath = fixture.Source };
            await viewModel.ScanAsync();
            var pending = viewModel.Items.Select(viewModel.EnsureThumbnailAsync).ToArray();
            await Task.WhenAll(pending);
            Assert.NotNull(viewModel.Items[^1].Thumbnail);
            Assert.InRange(viewModel.Items.Count(i => i.Thumbnail is not null), 1, 66);
            await viewModel.CancelAndWaitAsync();
        });
    }

    [Theory]
    [InlineData(1440, 920, "media-browser.png")]
    [InlineData(1120, 700, "media-browser-compact.png")]
    public async Task MainWindowRendersWithWorkingBindings(int windowWidth, int windowHeight, string screenshot)
    {
        await Wpf.Run(async () =>
        {
            using var fixture = Media();
            using var viewModel = new MainViewModel(Dispatcher.CurrentDispatcher) { SourcePath = fixture.Source };
            await viewModel.ScanAsync();
            var closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var trace = new BindingErrors();
            PresentationTraceSources.DataBindingSource.Listeners.Add(trace);
            var window = new MainWindow(viewModel)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Width = windowWidth,
                Height = windowHeight,
                Left = -10000,
                Top = -10000,
                ShowInTaskbar = false,
                ShowActivated = false,
            };
            try
            {
                window.Show();
                viewModel.CurrentItem = viewModel.Items.First(i => i.Extension == "PNG");
                await viewModel.CurrentPreview;
                viewModel.Items[0].IsChecked = true;
                viewModel.Items[2].IsChecked = true;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                Assert.True(window.ActualWidth <= SystemParameters.WorkArea.Width);
                Assert.True(window.ActualHeight <= SystemParameters.WorkArea.Height);
                var exportButton = (Button)window.FindName("ExportCheckedButton");
                var exportPosition = exportButton.TranslatePoint(new Point(), window);
                Assert.True(exportPosition.Y + exportButton.ActualHeight <= window.ActualHeight);
                Assert.NotNull(viewModel.PreviewImage);
                var artifacts = Environment.GetEnvironmentVariable("TMR_UI_ARTIFACTS");
                if (!string.IsNullOrEmpty(artifacts))
                {
                    Directory.CreateDirectory(artifacts);
                    ((ComboBox)window.FindName("SourcePathBox")).SetBinding(ComboBox.TextProperty,
                        new Binding { Source = "Example data / Telegram Desktop / tdata", Mode = BindingMode.OneWay });
                    window.UpdateLayout();
                    var content = (FrameworkElement)window.Content;
                    var width = (int)Math.Ceiling(content.ActualWidth + content.Margin.Left + content.Margin.Right);
                    var height = (int)Math.Ceiling(content.ActualHeight + content.Margin.Top + content.Margin.Bottom);
                    var visual = new DrawingVisual();
                    using (var drawing = visual.RenderOpen())
                    {
                        drawing.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
                        drawing.DrawRectangle(new VisualBrush(content), null,
                            new Rect(content.Margin.Left, content.Margin.Top, content.ActualWidth, content.ActualHeight));
                    }
                    var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                    image.Render(visual);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(image));
                    using var stream = File.Create(Path.Combine(artifacts, screenshot));
                    encoder.Save(stream);
                }
                Assert.Empty(trace.Errors);
            }
            finally
            {
                window.Closed += (_, _) => closed.TrySetResult(true);
                window.Close();
                await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                PresentationTraceSources.DataBindingSource.Listeners.Remove(trace);
            }
        });
    }

    private static RecoveryFixture Media()
    {
        var fixture = new RecoveryFixture();
        fixture.Add("cache", new CacheKey(0x780804, 1), Image(SKEncodedImageFormat.Png, false));
        fixture.Add("cache", new CacheKey(0x904, 1), Image(SKEncodedImageFormat.Webp, true));
        fixture.Add("cache", new CacheKey(30, 1), Sticker());
        fixture.Add("media_cache", new CacheKey(0x10000, 0x10000), RecoveryFixture.Wave(11));
        fixture.Add("media_cache", new CacheKey(0x10000, 0x20000), RecoveryFixture.Wave(22));
        fixture.Commit();
        return fixture;
    }
    private static byte[] Image(SKEncodedImageFormat format, bool warm)
    {
        using var bitmap = new SKBitmap(640, 420);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColor.Parse(warm ? "#F7E3D5" : "#CBE9FA"));
        using var paint = new SKPaint { Color = SKColor.Parse(warm ? "#EBA572" : "#F5D980"), IsAntialias = true };
        canvas.DrawCircle(470, 105, 48, paint);
        using var path = new SKPath();
        path.MoveTo(0, 330); path.LineTo(190, 110); path.LineTo(380, 340); path.LineTo(510, 210); path.LineTo(640, 345); path.LineTo(640, 420); path.LineTo(0, 420); path.Close();
        paint.Color = SKColor.Parse(warm ? "#B77D75" : "#68AAB5");
        canvas.DrawPath(path, paint);
        paint.Color = SKColor.Parse(warm ? "#93697B" : "#417B99");
        canvas.DrawRoundRect(new SKRect(0, 348, 640, 470), 70, 70, paint);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 88);
        return data.ToArray();
    }
    private static byte[] Sticker()
    {
        const string json = """
            {"v":"5.7.4","fr":30,"ip":0,"op":60,"w":256,"h":256,"nm":"Synthetic sticker","ddd":0,"assets":[],"layers":[{"ddd":0,"ind":1,"ty":4,"nm":"Circle","ks":{"o":{"a":0,"k":100},"r":{"a":0,"k":0},"p":{"a":0,"k":[128,128,0]},"a":{"a":0,"k":[0,0,0]},"s":{"a":0,"k":[100,100,100]}},"shapes":[{"ty":"el","p":{"a":0,"k":[0,0]},"s":{"a":0,"k":[140,140]}},{"ty":"fl","c":{"a":0,"k":[0.16,0.67,0.93,1]},"o":{"a":0,"k":100}}],"ip":0,"op":60,"st":0,"bm":0}]}
            """;
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true)) { gzip.Write(Encoding.UTF8.GetBytes(json)); }
        return output.ToArray();
    }

    private sealed class BindingErrors : TraceListener
    {
        public List<string> Errors { get; } = [];
        public override void Write(string? message) { }
        public override void WriteLine(string? message)
        {
            if (message?.Contains("Error", StringComparison.Ordinal) == true) { Errors.Add(message); }
        }
    }
}

internal static class Wpf
{
    private static readonly Dispatcher Ui = Start();
    public static Task Run(Func<Task> action) => Ui.InvokeAsync(action).Task.Unwrap();
    private static Dispatcher Start()
    {
        using var ready = new ManualResetEventSlim();
        Dispatcher? dispatcher = null;
        var thread = new Thread(() =>
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("/TelegramMediaRecovery;component/Themes/Controls.xaml", UriKind.Relative),
            });
            dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            ready.Set();
            Dispatcher.Run();
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return dispatcher!;
    }
}
