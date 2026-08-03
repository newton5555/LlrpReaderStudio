namespace LlrpReaderStudio.ViewModels;

public partial class AboutViewModel : PageViewModelBase
{
    public AboutViewModel()
    {
        PageTitle = "About";
    }

    public string AppName => "LLRP Reader Studio";
    public string Version => "0.4.0 (Net10.0 WPF / CommunityToolkit.Mvvm)";
    public string Description => "Modern .NET LLRP desktop workbench with reader discovery, inventory, tag memory access, and protocol-focused tooling.";
    public string LicenseNotice => "This project is for learning and UI/interaction study. Impinj, ItemTest, Speedway, RAIN RFID, and related names or marks belong to their respective owners. This application is not an official Impinj product and is not affiliated with or endorsed by Impinj.";
}
