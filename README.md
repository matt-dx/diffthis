# diffthis

Diff tool for git branches

```plaintext
DiffThis/
├── DiffThis.csproj          (net10.0-windows, MAUI, CommunityToolkit.Maui 13.0.0, CliWrap)
├── MauiProgram.cs           (DI registration, UseMauiCommunityToolkit)
├── App.xaml / .cs           (CreateWindow override, theme init from settings)
├── AppShell.xaml / .cs      (Shell routing: main, settings, branches, diff)
├── Models/                  Repository · DiffResult/DiffFile/DiffHunk/DiffLine · AppSettings
├── Services/
│   ├── GitService           (CliWrap → git diff/branch/rev-parse, full unified-diff parser)
│   ├── SettingsService      (Preferences API for theme, max-recent, repo list)
│   ├── ExportService        (Markdown + styled HTML generation)
│   └── DiffSessionService   (singleton to pass DiffResult across pages)
├── ViewModels/              (partial-property [ObservableProperty], RelayCommand)
│   ├── MainViewModel        (FolderPicker, recent repos, CLI arg handling)
│   ├── BranchSelectionViewModel  (QueryProperty, auto-selects main/master)
│   ├── DiffViewModel        (HTML diff, JS scroll-to-file, export commands)
│   └── SettingsViewModel    (theme + max-recent, live-save on change)
├── Views/
│   ├── MainPage             (hero header, recent repo list, remove button, CLI arg dispatch)
│   ├── BranchSelectionPage  (side-by-side pickers, swap button, loading overlay)
│   ├── DiffPage             (280px file sidebar + WebView, scroll-to-file on select)
│   └── SettingsPage         (theme toggle, max-recent picker, app version)
├── Converters/              (StringNotEmpty, FileStatusBadge, FileStatusColor, ThemeActiveColor)
└── Resources/               (Colors.xaml, Styles.xaml, SVG app icon + splash)
```
