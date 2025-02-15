﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using NuGet.Versioning;
using Squirrel.CommandLine.Commands;
using Squirrel.PropertyList;
using Squirrel.SimpleSplat;

namespace Squirrel.CommandLine.OSX
{
    class Commands
    {
        static IFullLogger Log => SquirrelLocator.Current.GetService<ILogManager>().GetLogger(typeof(Commands));

        public static void Bundle(BundleOsxCommand options)
        {
            var icon = options.Icon;
            var packId = options.PackId;
            var packDirectory = options.PackDirectory;
            var packVersion = options.PackVersion;
            var exeName = options.EntryExecutableName;
            var packAuthors = options.PackAuthors;
            var packTitle = options.PackTitle;

            var releaseDir = options.GetReleaseDirectory();

            Log.Info("Generating new '.app' bundle from a directory of application files.");

            var mainExePath = Path.Combine(packDirectory, exeName);
            if (!File.Exists(mainExePath))// || !PlatformUtil.IsMachOImage(mainExePath))
                throw new ArgumentException($"--exeName '{mainExePath}' does not exist or is not a mach-o executable.");

            var appleId = $"com.{packAuthors ?? packId}.{packId}";
            var escapedAppleId = Regex.Replace(appleId, @"[^\w\.]", "_");
            var appleSafeVersion = NuGetVersion.Parse(packVersion).Version.ToString();

            var info = new AppInfo {
                SQPackId = packId,
                SQPackAuthors = packAuthors,
                CFBundleName = packTitle ?? packId,
                //CFBundleDisplayName = packTitle ?? packId,
                CFBundleExecutable = exeName,
                CFBundleIdentifier = options.BundleId ?? escapedAppleId,
                CFBundlePackageType = "APPL",
                CFBundleShortVersionString = appleSafeVersion,
                CFBundleVersion = packVersion,
                CFBundleSignature = "????",
                NSPrincipalClass = "NSApplication",
                NSHighResolutionCapable = true,
                CFBundleIconFile = Path.GetFileName(icon),
            };

            Log.Info("Creating '.app' directory structure");
            var builder = new StructureBuilder(packId, releaseDir.FullName);
            if (Directory.Exists(builder.AppDirectory)) {
                Log.Warn(builder.AppDirectory + " already exists, deleting...");
                Utility.DeleteFileOrDirectoryHard(builder.AppDirectory);
            }

            builder.Build();

            Log.Info("Writing Info.plist");
            var plist = new PlistWriter(info, builder.ContentsDirectory);
            plist.Write();

            Log.Info("Copying resources into new '.app' bundle");
            File.Copy(icon, Path.Combine(builder.ResourcesDirectory, Path.GetFileName(icon)));

            Log.Info("Copying application files into new '.app' bundle");
            Utility.CopyFiles(new DirectoryInfo(packDirectory), new DirectoryInfo(builder.MacosDirectory));

            Log.Info("Bundle created successfully: " + builder.AppDirectory);
        }

        public static void Releasify(ReleasifyOsxCommand options)
        {
            var releaseDir = options.GetReleaseDirectory();

            var appBundlePath = options.BundleDirectory;
            Log.Info("Creating Squirrel application from app bundle at: " + appBundlePath);

            Log.Info("Parsing app Info.plist");
            var contentsDir = Path.Combine(appBundlePath, "Contents");

            if (!Directory.Exists(contentsDir))
                throw new Exception("Invalid bundle structure (missing Contents dir)");

            var plistPath = Path.Combine(contentsDir, "Info.plist");
            if (!File.Exists(plistPath))
                throw new Exception("Invalid bundle structure (missing Info.plist)");

            NSDictionary rootDict = (NSDictionary) PropertyListParser.Parse(plistPath);
            var packId = rootDict.ObjectForKey(nameof(AppInfo.SQPackId))?.ToString();
            if (String.IsNullOrWhiteSpace(packId))
                packId = rootDict.ObjectForKey(nameof(AppInfo.CFBundleIdentifier))?.ToString();

            var packAuthors = rootDict.ObjectForKey(nameof(AppInfo.SQPackAuthors))?.ToString();
            if (String.IsNullOrWhiteSpace(packAuthors))
                packAuthors = packId;

            var packTitle = rootDict.ObjectForKey(nameof(AppInfo.CFBundleName))?.ToString();
            var packVersion = rootDict.ObjectForKey(nameof(AppInfo.CFBundleVersion))?.ToString();

            if (String.IsNullOrWhiteSpace(packId))
                throw new InvalidOperationException($"Invalid CFBundleIdentifier in Info.plist: '{packId}'");

            if (String.IsNullOrWhiteSpace(packTitle))
                throw new InvalidOperationException($"Invalid CFBundleName in Info.plist: '{packTitle}'");

            if (String.IsNullOrWhiteSpace(packVersion) || !NuGetVersion.TryParse(packVersion, out var _))
                throw new InvalidOperationException($"Invalid CFBundleVersion in Info.plist: '{packVersion}'");

            Log.Info($"Package valid: '{packId}', Name: '{packTitle}', Version: {packVersion}");

            Log.Info("Adding Squirrel resources to bundle.");
            var nuspecText = NugetConsole.CreateNuspec(
                packId, packTitle, packAuthors, packVersion, options.ReleaseNotes, options.IncludePdb);
            var nuspecPath = Path.Combine(contentsDir, Utility.SpecVersionFileName);

            // nuspec and UpdateMac need to be in contents dir or this package can't update
            File.WriteAllText(nuspecPath, nuspecText);
            File.Copy(HelperExe.UpdateMacPath, Path.Combine(contentsDir, "UpdateMac"), true);

            var zipPath = Path.Combine(releaseDir.FullName, $"{packId}-{options.TargetRuntime.StringWithNoVersion}.zip");
            if (File.Exists(zipPath)) File.Delete(zipPath);

            // code signing all mach-o binaries
            if (SquirrelRuntimeInfo.IsOSX && !String.IsNullOrEmpty(options.SigningAppIdentity) && !String.IsNullOrEmpty(options.NotaryProfile)) {
                HelperExe.CodeSign(options.SigningAppIdentity, options.SigningEntitlements, appBundlePath);
                HelperExe.CreateDittoZip(appBundlePath, zipPath);
                HelperExe.Notarize(zipPath, options.NotaryProfile);
                HelperExe.Staple(appBundlePath);
                HelperExe.SpctlAssessCode(appBundlePath);
                File.Delete(zipPath);
            } else if (SquirrelRuntimeInfo.IsOSX && !String.IsNullOrEmpty(options.SigningAppIdentity)) {
                HelperExe.CodeSign(options.SigningAppIdentity, options.SigningEntitlements, appBundlePath);
                Log.Warn("Package was signed but will not be notarized or verified. Must supply the --notaryProfile option.");
            } else if (SquirrelRuntimeInfo.IsOSX) {
                Log.Warn("Package will not be signed or notarized. Requires the --signAppIdentity and --notaryProfile options.");
            } else {
                Log.Warn("Package will not be signed or notarized. Only supported on OSX.");
            }

            // create a portable zip package from signed/notarized bundle
            Log.Info("Creating final application artifact (zip)");
            if (SquirrelRuntimeInfo.IsOSX) {
                HelperExe.CreateDittoZip(appBundlePath, zipPath);
            } else {
                Log.Warn("Could not create executable zip with ditto. Only supported on OSX.");
                EasyZip.CreateZipFromDirectory(zipPath, appBundlePath, nestDirectory: true);
            }

            // create release / delta from notarized .app
            Log.Info("Creating Squirrel Release");
            using var _ = Utility.GetTempDirectory(out var tmp);
            var nupkgPath = NugetConsole.CreatePackageFromNuspecPath(tmp, appBundlePath, nuspecPath);

            var releaseFilePath = Path.Combine(releaseDir.FullName, "RELEASES");
            var releases = new Dictionary<string, ReleaseEntry>();

            ReleaseEntry.BuildReleasesFile(releaseDir.FullName);
            foreach (var rel in ReleaseEntry.ParseReleaseFile(File.ReadAllText(releaseFilePath, Encoding.UTF8))) {
                releases[rel.Filename] = rel;
            }

            var rp = new ReleasePackageBuilder(nupkgPath);
            var suggestedName = ReleasePackageBuilder.GetSuggestedFileName(packId, packVersion, options.TargetRuntime.StringWithNoVersion);
            var newPkgPath = rp.CreateReleasePackage((i, pkg) => Path.Combine(releaseDir.FullName, suggestedName));

            Log.Info("Creating Delta Packages");
            var prev = ReleasePackageBuilder.GetPreviousRelease(releases.Values, rp, releaseDir.FullName, options.TargetRuntime);
            if (prev != null && !options.NoDelta) {
                var deltaBuilder = new DeltaPackageBuilder();
                var deltaFile = rp.ReleasePackageFile.Replace("-full", "-delta");
                var dp = deltaBuilder.CreateDeltaPackage(prev, rp, deltaFile);
                var deltaEntry = ReleaseEntry.GenerateFromFile(deltaFile);
                releases[deltaEntry.Filename] = deltaEntry;
            }

            var fullEntry = ReleaseEntry.GenerateFromFile(newPkgPath);
            releases[fullEntry.Filename] = fullEntry;

            ReleaseEntry.WriteReleaseFile(releases.Values, releaseFilePath);

            // create installer package, sign and notarize
            if (!options.NoPackage) {
                if (SquirrelRuntimeInfo.IsOSX) {
                    var pkgPath = Path.Combine(releaseDir.FullName, $"{packId}-{options.TargetRuntime.StringWithNoVersion}.pkg");

                    Dictionary<string, string> pkgContent = new() {
                        {"welcome", options.PackageWelcome },
                        {"license", options.PackageLicense },
                        {"readme", options.PackageReadme },
                        {"conclusion", options.PackageConclusion },
                    };

                    HelperExe.CreateInstallerPkg(appBundlePath, packTitle, pkgContent, pkgPath, options.SigningInstallIdentity);
                    if (!String.IsNullOrEmpty(options.SigningInstallIdentity) && !String.IsNullOrEmpty(options.NotaryProfile)) {
                        HelperExe.Notarize(pkgPath, options.NotaryProfile);
                        HelperExe.Staple(pkgPath);
                        HelperExe.SpctlAssessInstaller(pkgPath);
                    } else {
                        Log.Warn("Package installer (.pkg) will not be Notarized. " +
                                 "This is supported with the --signInstallIdentity and --notaryProfile arguments.");
                    }
                } else {
                    Log.Warn("Package installer (.pkg) will not be created - this is only supported on OSX.");
                }
            }

            Log.Info("Done.");
        }
    }
}