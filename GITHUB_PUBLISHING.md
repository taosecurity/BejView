# Publishing BejView on GitHub

This guide will walk you through the process of publishing your BejView project on GitHub.

## Prerequisites

1. [Create a GitHub account](https://github.com/join) if you don't already have one
2. [Install Git](https://git-scm.com/downloads) on your computer
3. Configure Git with your username and email:
   ```
   git config --global user.name "Your Name"
   git config --global user.email "your.email@example.com"
   ```

## Step 1: Initialize a Git Repository

1. Open a terminal/command prompt
2. Navigate to your project directory:
   ```
   cd "C:\Users\Richard Bejtlich\Downloads\VisualStudioCode\BejView"
   ```
3. Initialize a Git repository:
   ```
   git init
   ```

## Step 2: Create a .gitignore File

Create a .gitignore file to exclude build artifacts and other unnecessary files:

```
# .NET build artifacts
bin/
obj/
*.user
*.suo
*.vs/
.vs/

# Build results
[Dd]ebug/
[Rr]elease/
x64/
x86/
[Aa][Rr][Mm]/
[Aa][Rr][Mm]64/
bld/
[Bb]in/
[Oo]bj/
[Ll]og/
[Ll]ogs/

# Visual Studio files
.vs/
*.userprefs
*.usertasks
*.vscode/
*.idea/
*.sln.iml

# User-specific files
*.rsuser
*.suo
*.user
*.userosscache
*.sln.docstates

# NuGet Packages
*.nupkg
# NuGet Symbol Packages
*.snupkg
# The packages folder can be ignored because of Package Restore
**/[Pp]ackages/*
# except build/, which is used as an MSBuild target.
!**/[Pp]ackages/build/
# Uncomment if necessary however generally it will be regenerated when needed
#!**/[Pp]ackages/repositories.config
# NuGet v3's project.json files produces more ignorable files
*.nuget.props
*.nuget.targets
```

## Step 3: Add and Commit Your Files

1. Add all your project files to the Git repository:
   ```
   git add .
   ```

2. Commit the files:
   ```
   git commit -m "Initial commit of BejView project"
   ```

## Step 4: Create a GitHub Repository

1. Go to [GitHub](https://github.com/)
2. Click on the "+" icon in the top-right corner and select "New repository"
3. Enter "BejView" as the repository name
4. Add a description: "A Windows application for efficiently viewing and searching large text files"
5. Choose whether to make the repository public or private
6. Do NOT initialize the repository with a README, .gitignore, or license (we'll push our existing files)
7. Click "Create repository"

## Step 5: Connect Your Local Repository to GitHub

After creating the repository, GitHub will show you commands to push an existing repository. Use the HTTPS or SSH URL provided:

```
git remote add origin https://github.com/YourUsername/BejView.git
git branch -M main
git push -u origin main
```

Replace `YourUsername` with your actual GitHub username.

## Step 6: Create a Release with the Executable

1. Go to your GitHub repository page
2. Click on "Releases" in the right sidebar
3. Click "Create a new release"
4. Enter a tag version (e.g., "v1.0.0")
5. Enter a release title (e.g., "BejView 1.0.0")
6. Add release notes describing the features
7. Drag and drop the BejView.exe file from `C:\Users\Richard Bejtlich\Downloads\VisualStudioCode\BejView\bin\Release\net9.0-windows\win-x64\publish\` to the "Attach binaries" section
   - Note: GitHub has a file size limit of 100MB for individual files, so you may need to use [Git LFS](https://git-lfs.github.com/) for the executable or create a compressed archive
8. Click "Publish release"

## Step 7: Update Your Repository

Whenever you make changes to your project:

1. Add the changed files:
   ```
   git add .
   ```

2. Commit the changes:
   ```
   git commit -m "Description of changes"
   ```

3. Push to GitHub:
   ```
   git push
   ```

## Using GitHub Desktop (Alternative)

If you prefer a GUI over command line:

1. Download and install [GitHub Desktop](https://desktop.github.com/)
2. Sign in with your GitHub account
3. Add your local repository (File > Add local repository)
4. Publish the repository to GitHub
5. Use the interface to commit and push changes

## GitHub Pages (Optional)

You can create a project website using GitHub Pages:

1. Go to your repository settings
2. Scroll down to the "GitHub Pages" section
3. Select the "main" branch and "/docs" folder as the source
4. Create a "docs" folder in your repository with HTML/CSS/JS files for your website
5. Your site will be available at `https://YourUsername.github.io/BejView`

## GitHub Actions (Optional)

You can set up GitHub Actions to automatically build your project when you push changes:

1. Create a `.github/workflows` directory in your repository
2. Create a YAML file (e.g., `build.yml`) with build instructions
3. Push these changes to GitHub
4. GitHub will automatically run the workflow when you push changes

Example workflow file for .NET:

```yaml
name: .NET Build

on:
  push:
    branches: [ main ]
  pull_request:
    branches: [ main ]

jobs:
  build:
    runs-on: windows-latest
    steps:
    - uses: actions/checkout@v2
    - name: Setup .NET
      uses: actions/setup-dotnet@v1
      with:
        dotnet-version: 9.0.x
    - name: Restore dependencies
      run: dotnet restore
    - name: Build
      run: dotnet build --no-restore
    - name: Test
      run: dotnet test --no-build --verbosity normal
