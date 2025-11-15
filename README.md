# UnityProjectAnalyzer

A simple command line tool that analyzes the directory of a Unity project and creates at a provided output directory:
- For each .unity scene file in the project a .unity.dump text file that illustrates the GameObject hierarchy inside the scene;
- An UnusedScripts.csv file listing the path and GUID of each script in the project that is neither attached to any GameObject in any project scene nor is it exposed in the editor through another script.

## Example Usage
### On Windows x64:
```Powershell
cd <install folder>
.\UnityProjectAnalyzer_Windows_x64.exe <project directory> <output directory>
```

### On Linux x64
```Bash
cd <install folder>
UnityProjectAnalyzer_Linux_x64 <project directory> <output directory>
```

## Dependencies
If you want to build the project yourself, the current dependencies are:
- [.NET SDK 10.0](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
- [YamlDotNet](https://github.com/aaubry/YamlDotNet)
- [CsvHelper](https://joshclose.github.io/CsvHelper/)
