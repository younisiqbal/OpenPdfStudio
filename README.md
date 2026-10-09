# OpenPdfStudio

OpenPdfStudio is a desktop PDF viewer built with [Avalonia](https://avaloniaui.net/) and [PDFium](https://pdfium.googlesource.com/pdfium/). It runs on .NET 10.

## Features

- Open PDF files, including password-protected documents.
- Home screen with recent files and starred files.
- Open several documents at once in tabs.
- Continuous scrolling through pages, with a page thumbnail sidebar.
- Zoom from 25% to 400%, plus fit width, fit page, and actual size.
- Rotate the page view.
- Hand tool for panning, with a choice of cursor styles.
- Select text on a page and copy it to the clipboard.
- Themes: Dark, Light, System, Midnight, and High Contrast.

### Keyboard shortcuts

| Shortcut | Action |
| --- | --- |
| Ctrl+O | Open a file |
| Ctrl+W | Close the current tab |
| Ctrl+Plus | Zoom in |
| Ctrl+Minus | Zoom out |
| Ctrl+C | Copy selected text |

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)

## Build and run

From the repository root:

```bash
dotnet run
```

To build a release version:

```bash
dotnet build -c Release
```

## Tech stack

- .NET 10
- Avalonia 12.1.3 with the Fluent theme
- CommunityToolkit.Mvvm for view models and commands
- PDFiumCore for rendering pages and reading text

PDFium calls run on a dedicated background worker, so rendering does not block the UI.

## Project layout

- `Models` holds small data types such as recent files, viewer tools, and text selections.
- `Services/Pdf` holds the PDFium worker, document session, page renderer, and bitmap cache.
- `ViewModels` holds the home screen, document, and page view models.
- `Views` holds the main window, home screen, document viewer, toolbars, and dialogs.
- `Styles` and `Themes` hold colors, icons, control styles, and theme variants.

## License

Licensed under the Apache License 2.0. See the `LICENSE` file on the `main` branch.
