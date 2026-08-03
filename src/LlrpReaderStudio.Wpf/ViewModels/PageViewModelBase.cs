using CommunityToolkit.Mvvm.ComponentModel;

namespace LlrpReaderStudio.ViewModels;

public abstract partial class PageViewModelBase : ObservableObject
{
    [ObservableProperty]
    private string pageTitle = string.Empty;
}
