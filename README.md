# OpenPdfStudio

OpenPdfStudio is a desktop PDF viewer built with [Avalonia](https://avaloniaui.net/) and [PDFium](https://pdfium.googlesource.com/pdfium/). It runs on .NET 10.

Website: [openpdfstudio.github.io](https://openpdfstudio.github.io/)

## Download

Get the latest build for Windows, Linux, or macOS from the [Releases page](https://github.com/younisiqbal/OpenPdfStudio/releases/latest). Each download is a self-contained zip, so you don't need .NET installed. Unzip it and run `OpenPdfStudio`.

The macOS build is not signed yet. The first time you open it, right-click the app and choose Open.

## Features

- Open PDF files, including password-protected documents. Drag one or more PDFs onto the window to open them. Dropping a Word document (`.docx`) offers to convert it to PDF.
- New Task home screen: a Recent/Starred sidebar and task cards for opening, organizing, splitting, merging, converting to or from Word, and comparing PDFs.
- Open several documents at once in tabs.
- Ribbon-style document view: quick Open, Save, Print, Undo, Redo, and Hand/Select buttons in the title bar, a File menu, and Home, Edit, Comment, View, Protect, and Help tabs. Hand and Select lead every tab. The Home tab includes Convert (To Word / From Word). Document tabs sit under the ribbon, with a Start tab for the home screen. Draw, Picture, Erase, Signature, Redact, password, and compress are shown as coming soon.
- Three page layouts from the bottom bar: continuous scroll, single page, and two pages side by side (the first page on its own, like a book cover). In single-page layout, scrolling past the end of a page turns to the next one. The layout you pick is remembered.
- A side panel with page thumbnails and bookmarks, opened from the Bookmarks and Pages icons on the left strip or the View tab (the strip's search icon opens Find). The Bookmarks panel shows the PDF's own outline and your bookmarks. Add a bookmark from the panel or from the popup that appears when you select text. PDFium can't write outline entries, so your bookmarks are kept by OpenPdfStudio for each file (in `bookmarks.json` in the app's local data folder) rather than in the PDF.
- Zoom from 25% to 400%, plus fit width, fit page, and actual size.
- Rotate the page view.
- Hand tool for panning, with a choice of cursor styles.
- Select text on a page and copy it to the clipboard.
- Snapshot tool: drag a box over any part of a page to copy that area to the clipboard as an image.
- Comment tools: Note, Typewriter, Textbox, Highlight, Underline, Strikeout and Squiggly. They are saved as real PDF annotations. Notes open a yellow pellet you can drag independently of the icon. A Format panel (opened from Format, Properties, the pellet text, or note settings) sets font, size, colour, bold/italic/underline/strikeout, alignment, opacity, note icon, and border/fill for the whole annotation. Undo and Redo cover annotation changes.
- Right-click a page for Paste, Read Mode, Full Screen, zoom and fit options, Rotate Right, Print, and Rulers.
- Paste text or an image from the clipboard onto a page. Paste puts it where you right-clicked (or under the pointer). Paste in Place puts text copied from a PDF back at the same spot. Pasted text uses Helvetica, so it covers Western European characters only.
- Save (Ctrl+S) and Save As (Ctrl+Shift+S) keep pasted changes. A dot on the tab marks unsaved changes, and you are asked before a modified file is closed.
- Read Mode (Ctrl+H) and Full Screen (F11) hide the menus, tabs, toolbars, and thumbnails. Press Esc to leave.
- Print (Ctrl+P) with a choice of printer, copies, page range, and orientation. Printing uses the Windows print system on Windows and CUPS (`lp`) on Linux and macOS.
- Rulers along the top and left of the page that measure the current page in inches or centimeters. Turn them on from the View tab or Ctrl+R, and right-click a ruler to change the unit.
- Find box (Ctrl+F) in the top-right corner of the page. It highlights every match, steps through them with Previous and Next, and has a gear menu with Whole Words Only, Case Sensitive, Include Page Text, Include Bookmarks, Include Comments, and Highlight All Text.
- Search box in the title bar with recently used searches, Show matching text results, Advanced Search, and Search & Highlight.
- Advanced Search panel. Search the current document, all open documents, or a folder of PDFs. Match the exact phrase, any of the words, or all of the words. Optionally include bookmarks, comments, and attachment names, and use stemming to find other forms of a word. Results are grouped by file, and clicking one jumps to the match.
- Organize pages (Edit tab > Organize) right in the document tab: the page area opens into every page at once. Drag pages to reorder them, rotate or delete them, and insert blank pages (sized like the page before). Select with a click, Ctrl or Shift-click, a box dragged with the mouse, or the arrow keys (Shift extends). Switch between grid and list views and small, medium, or large thumbnails. Nothing changes until Apply; Esc cancels. Rotation and new pages are written into the PDF and kept when you save.
- Split a PDF into one file per page, or one file per page range (for example `1-3; 4-7; 8-`).
- Merge several PDFs into one, in the order you choose. Password-protected files ask for their password.
- Convert a PDF to an editable Word document (`.docx`) from the home screen, the Home tab Convert group, or File > Export to Word. Text, fonts, colours, and images are preserved where possible; scanned pages become full-page pictures. Only `.docx` is written.
- Create a PDF from a Word document (`.docx`) from the home screen, the Home tab, or File > Create PDF from Word. Paragraphs, styles, lists, tables, images, and headers/footers are laid out into pages. The old binary `.doc` format is not supported; save as `.docx` first.
- Compare two PDFs side by side from the home screen. Pages stay in sync as you navigate (optional), and a list shows which pages differ by text, plus pages that exist only on the left or right.
- Settings (File > Settings) for theme, default page layout, hand cursor, ruler, comment tools, and the ribbon.
- Help tab with links to the documentation and the issue tracker, and an About box.
- Themes: Dark, Light, System, Midnight, and High Contrast.

Adding a password and compressing PDFs are not available yet.

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
| Esc | Close the find box, cancel the snapshot tool, or leave Read Mode and Full Screen |

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
- PDFiumCore for rendering pages, reading text, and editing pages
- DocumentFormat.OpenXml for reading and writing `.docx`
- QuestPDF for laying out Word documents as PDF pages

PDFium calls run on a dedicated background worker, so rendering does not block the UI.

## Project layout

- `Models` holds small data types such as recent files, viewer tools, and text selections.
- `Services/Pdf` holds the PDFium worker, document session, page renderer, bitmap cache, and search engine.
- `Services/Convert` holds Word and PDF conversion (Open XML read/write and QuestPDF layout).
- `ViewModels` holds the home screen, document, page, and search view models.
- `Views` holds the main window, home screen, document viewer, toolbars, and dialogs.
- `Styles` and `Themes` hold colors, icons, control styles, and theme variants.
## Releases

Pushing a tag such as `v0.1.0` runs the release workflow. It publishes self-contained builds for `win-x64`, `linux-x64`, and `osx-arm64` and attaches them to a GitHub Release.

## License

Licensed under the Apache License 2.0. See the `LICENSE` file on the `main` branch.
