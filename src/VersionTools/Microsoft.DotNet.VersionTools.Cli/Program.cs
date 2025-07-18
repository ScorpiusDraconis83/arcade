// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.CommandLine;
using Microsoft.Arcade.Common;

namespace Microsoft.DotNet.VersionTools.Cli;

class Program
{
    static int Main(string[] args)
    {
        //// Global options

        RootCommand rootCommand = new("Microsoft.DotNet.VersionTools.Cli v" + Environment.Version.ToString(2))
        {
            TreatUnmatchedTokensAsErrors = true
        };

        // Package command
        Option<string> assetsDirectoryOption = new("--assets-path", "-d")
        {
            Description = "Path to the directory where the assets are located",
            Required = true
        };

        Option<string> searchPatternOption = new("--search-pattern", "-s")
        {
            Description = "The search string to match against the names of subdirectories in --assets-path. See Directory.GetFiles for details.",
            DefaultValueFactory = _ => "*.nupkg"
        };

        Option<bool> recursiveOption = new("--recursive", "-r")
        {
            Description = "Search for assets recursively.",
            DefaultValueFactory = _ => true
        };

        System.CommandLine.Command trimAssetVersionCommand = new("trim-assets-version", "Trim versions from provided assets. Currently, only NuGet packages are supported.");
        trimAssetVersionCommand.Options.Add(assetsDirectoryOption);
        trimAssetVersionCommand.Options.Add(searchPatternOption);
        trimAssetVersionCommand.Options.Add(recursiveOption);
        trimAssetVersionCommand.SetAction(result =>
        {
            var operation = new VersionTrimmingOperation(
                new VersionTrimmingOperation.Context
                {
                    NupkgInfoFactory = new NupkgInfoFactory(new PackageArchiveReaderFactory()),
                    DirectoryProxy = new DirectoryProxy(),
                    FileProxy = new FileProxy(),
                    Logger = new ConsoleLogger(),

                    AssetsDirectory = result.GetValue(assetsDirectoryOption),
                    SearchPattern = result.GetValue(searchPatternOption),
                    Recursive = result.GetValue(recursiveOption)
                });
            return (int)operation.Execute();
        });

        rootCommand.Subcommands.Add(trimAssetVersionCommand);
        return rootCommand.Parse(args).Invoke();
    }
}
