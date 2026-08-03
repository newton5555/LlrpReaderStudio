using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LlrpReaderStudio.ViewModels;
using MahApps.Metro.Controls;

namespace LlrpReaderStudio;

public partial class MainWindow : MetroWindow
{
    private readonly MainViewModel viewModel;
    private bool canClose;
    private Task? closeTask;

    public MainWindow(MainViewModel viewModel)
    {
        this.viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        Closing += OnClosing;
    }

    private void OnWindowMouseLeftButtonDown(object sender, MouseButtonEventArgs args)
    {
        if (args.ChangedButton != MouseButton.Left || IsInteractiveElement(args.OriginalSource as DependencyObject))
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // DragMove can throw if the mouse button is released during event dispatch.
        }
    }

    private async void OnClosing(object? sender, CancelEventArgs args)
    {
        if (canClose)
        {
            return;
        }

        args.Cancel = true;
        IsEnabled = false;

        closeTask ??= DisposeViewModelAsync();

        try
        {
            await closeTask;
        }
        catch
        {
            // The window must remain closable if a reader has already lost its transport.
        }
        finally
        {
            canClose = true;
            try
            {
                await Dispatcher.BeginInvoke(
                    DispatcherPriority.ApplicationIdle,
                    new Action(() =>
                    {
                        if (IsLoaded)
                        {
                            Close();
                        }
                    }));
            }
            catch (InvalidOperationException)
            {
                // The dispatcher can already be shutting down.
            }
        }
    }

    private async Task DisposeViewModelAsync() => await viewModel.DisposeAsync();

    private static bool IsInteractiveElement(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ButtonBase or TextBoxBase or Selector or RangeBase or ScrollViewer)
            {
                return true;
            }

            if (source is MainWindow)
            {
                return false;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }
}
