# OpenPdfStudio

OpenPdfStudio is a desktop PDF viewer built with [Avalonia](https://avaloniaui.net/) and [PDFium](https://pdfium.googlesource.com/pdfium/). It runs on .NET 10.

Website: [openpdfstudio.github.io](https://openpdfstudio.github.io/)

## Download

Get the latest build for Windows, Linux, or macOS from the [Releases page](https://github.com/younisiqbal/OpenPdfStudio/releases/latest). Each download is a self-contained zip, so you don't need .NET installed. Unzip it and run `OpenPdfStudio`.

The macOS build is not signed yet. The first time you open it, right-click the app and choose Open.

## Features

- Open PDF files, including password-protected documents.
- Home screen with recent files and starred files.
- Open several documents at once in tabs.
- Continuous scrolling through pages, with a page thumbnail sidebar.
- Zoom from 25% to 400%, plus fit width, fit page, and actual size.
- Rotate the page view.
- Hand tool for panning, with a choice of cursor styles.
- Select text on a page and copy it to the clipboard.
- Comment tools: Note, Typewriter, Textbox, Callout, Highlight, Underline, Strikeout and Squiggly. They are saved as real PDF annotations. A Format panel sets font, size, colour, bold/italic/underline/strikeout, alignment, opacity, note icon, and border/fill for the whole annotation. Undo and Redo cover annotation changes.
- Right-click a page for Paste, Read Mode, Full Screen, zoom and fit options, Rotate Right, Print, and Rulers.
- Paste text or an image from the clipboard onto a page. Paste puts it where you right-clicked (or under the pointer). Paste in Place puts text copied from a PDF back at the same spot. Pasted text uses Helvetica, so it covers Western European characters only.
- Save (Ctrl+S) and Save As (Ctrl+Shift+S) keep pasted changes. A dot on the tab marks unsaved changes, and you are asked before a modified file is closed.
- Read Mode (Ctrl+H) and Full Screen (F11) hide the menus, tabs, toolbars, and thumbnails. Press Esc to leave.
- Print (Ctrl+P) with a choice of printer, copies, page range, and orientation. Printing uses the Windows print system on Windows and CUPS (`lp`) on Linux and macOS.
- Rulers along the top and left of the page that measure the current page in inches or centimeters. Turn them on from the Ruler button in the left toolbar, View > Ruler, or Ctrl+R, and right-click a ruler to change the unit.
- Find box (Ctrl+F) in the top-right corner of the page. It highlights every match, steps through them with Previous and Next, and has a gear menu with Whole Words Only, Case Sensitive, Include Page Text, Include Bookmarks, Include Comments, and Highlight All Text.
- Search box in the tab row with recently used searches, Show matching text results, Advanced Search, and Search & Highlight.
- Advanced Search panel. Search the current document, all open documents, or a folder of PDFs. Match the exact phrase, any of the words, or all of the words. Optionally include bookmarks, comments, and attachment names, and use stemming to find other forms of a word. Results are grouped by file, and clicking one jumps to the match.
- Themes: Dark, Light, System, Midnight, and High Contrast.

### Keyboard shortcuts

| Shortcut | Action |
| --- | --- |
| Ctrl+O | Open a file |
| Ctrl+W | Close the current tab |
| Ctrl+S | Save |
| Ctrl+Shift+S | Save As |
| Ctrl+P | Print |
| Ctrl+Plus | Zoom in |
| Ctrl+Minus | Zoom out |
| Ctrl+1 | Actual size |
| Ctrl+0 | Fit page |
| Ctrl+2 | Fit width |
| Ctrl+Shift+Plus | Rotate the view right |
| Ctrl+Z | Undo the last annotation change |
| Ctrl+Y | Redo |
| Delete | Delete the selected annotation |
| Ctrl+C | Copy selected text |
| Ctrl+V | Paste onto the page |
| Ctrl+Alt+V | Paste in place |
| Ctrl+H | Read Mode |
| F11 | Full Screen |
| Ctrl+R | Show or hide the ruler |
| Ctrl+F | Open the find box |
| F3 / Enter | Next match |
| Shift+F3 / Shift+Enter | Previous match |
| Ctrl+Shift+F | Open Advanced Search |
| Esc | Close the find box, or leave Read Mode and Full Screen |

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
- `Services/Pdf` holds the PDFium worker, document session, page renderer, bitmap cache, and search engine.
- `ViewModels` holds the home screen, document, page, and search view models.
- `Views` holds the main window, home screen, document viewer, toolbars, and dialogs.
- `Styles` and `Themes` hold colors, icons, control styles, and theme variants.
## Releases

Pushing a tag such as `v0.1.0` runs the release workflow. It publishes self-contained builds for `win-x64`, `linux-x64`, and `osx-arm64` and attaches them to a GitHub Release.

## License

Licensed under the Apache License 2.0. See the `LICENSE` file on the `main` branch.
