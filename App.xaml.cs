using DiffThis.Services;

namespace DiffThis;

public partial class App : Application
{
    private readonly MainPage _mainPage;

    public App(ISettingsService settings, MainPage mainPage)
    {
        InitializeComponent();
        UserAppTheme = settings.Theme;
        _mainPage = mainPage;
    }

    protected override Window CreateWindow(IActivationState? activationState) =>
        new Window(_mainPage) { Title = "DiffThis" };
}
