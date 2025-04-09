# BejView - Text File Viewer

## Overview
BejView is a Windows application designed for efficiently viewing and searching large text files. It uses memory-mapped files and virtualized rendering to handle files of any size with minimal memory usage.

The limit appears to be the RAM of the system running BejView. I tested the program with a 26 GB text file on a system with 64 GB RAM.

## Features
- Open and view large text files with minimal memory usage
- Fast search functionality with context display
- Line numbers displayed in a separate column
- Dark mode toggle (Ctrl+D)
- Font size adjustment (Ctrl++ and Ctrl+-)
- Recent files list
- RAM usage meter
- Navigation shortcuts:
  - Ctrl+B to go to the beginning of the file
  - Ctrl+E to go to the end of the file
  - Ctrl+F to open search panel
  - Ctrl+N to go to next search result
  - Ctrl+P to go to previous search result

## System Requirements
- Windows operating system
- .NET 9.0 or higher

## Installation
No installation required. Simply download and run the executable.

## Usage
1. Launch BejView
2. Use File > Open (Ctrl+O) to open a text file
3. Use the search functionality (Ctrl+F) to find text within the file
4. Toggle dark mode with Ctrl+D
5. Adjust font size with Ctrl++ and Ctrl+-
6. View recently opened files from the File > Recent Files menu

## Keyboard Shortcuts
- Ctrl+O: Open file
- Ctrl+F: Find text
- Ctrl+D: Toggle dark mode
- Ctrl+N: Go to next search result
- Ctrl+P: Go to previous search result
- Ctrl+B: Go to beginning of file
- Ctrl+E: Go to end of file
- Ctrl++: Increase font size
- Ctrl+-: Decrease font size
- Alt+F4: Exit application

## Technical Details
BejView uses several optimization techniques to handle large files efficiently:
- Memory-mapped files for efficient file access
- Virtualized text rendering that only loads visible portions of the file
- Line position indexing for fast navigation
- Caching of frequently accessed lines
- Chunked processing for search operations

## License
MIT License

## Credits
Developed by Richard Bejtlich using Visual Studio Code, Cline, OpenRouter.ai, and Claude 3.7.

## Version
1.0
