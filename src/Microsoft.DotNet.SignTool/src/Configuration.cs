// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Versioning;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Microsoft.DotNet.StrongName;

namespace Microsoft.DotNet.SignTool
{
    internal class Configuration
    {
        private readonly TaskLoggingHelper _log;

        private readonly List<ItemToSign> _itemsToSign;

        /// <summary>
        /// This store content information for container files.
        /// Key is the content hash of the file.
        /// </summary>
        private readonly Dictionary<SignedFileContentKey, ZipData> _zipDataMap;

        /// <summary>
        /// Path to where container files will be extracted.
        /// </summary>
        private readonly string _pathToContainerUnpackingDirectory;

        /// <summary>
        /// This enable the overriding of the default certificate for a given file+token+target_framework.
        /// It also contains a SignToolConstants.IgnoreFileCertificateSentinel flag in the certificate name in case the file does not need to be signed
        /// for that 
        /// </summary>
        private readonly Dictionary<ExplicitCertificateKey, string> _fileSignInfo;

        /// <summary>
        /// Used to look for signing information when we have the PublicKeyToken of a file.
        /// </summary>
        private readonly Dictionary<string, List<SignInfo>> _strongNameInfo;

        /// <summary>
        /// A list of all the binaries that MUST be signed. Also include containers that don't need 
        /// to be signed themselves but include files that must be signed.
        /// </summary>
        private readonly List<FileSignInfo> _filesToSign;

        private List<WixPackInfo> _wixPacks;

        /// <summary>
        /// Mapping of ".ext" to certificate. Files that have an extension on this map
        /// will be signed using the specified certificate. Input list might contain
        /// duplicate entries
        /// </summary>
        private readonly Dictionary<string, List<SignInfo>> _fileExtensionSignInfo;

        private readonly Dictionary<SignedFileContentKey, FileSignInfo> _filesByContentKey;

        /// <summary>
        /// For each uniquely identified file keeps track of all containers where the file appeared.
        /// </summary>
        private readonly Dictionary<SignedFileContentKey, HashSet<string>> _whichPackagesTheFileIsIn;

        /// <summary>
        /// Keeps track of all files that produced a given error code.
        /// </summary>
        private readonly Dictionary<SigningToolErrorCode, HashSet<SignedFileContentKey>> _errors;

        /// <summary>
        /// This is a list of the friendly name of certificates that can be used to
        /// sign already signed binaries.
        /// </summary>
        private readonly Dictionary<string, List<AdditionalCertificateInformation>> _additionalCertificateInformation;

        /// <summary>
        /// Use the content hash in the path of the extracted file paths. 
        /// The default is to use a unique content id based on the number of items extracted.
        /// </summary>
        private readonly bool _useHashInExtractionPath;

        /// <summary>
        /// A list of files whose content needs to be overwritten by signed content from a different file.
        /// Copy the content of file with full path specified in Key to file with full path specified in Value.
        /// </summary>
        internal List<KeyValuePair<string, string>> _filesToCopy;

        /// <summary>
        /// Maps file hashes to collision ids. We use this to determine whether we processed an asset already
        /// and what collision id to use. We always choose the lower collision id in case of collisions.
        /// </summary>
        internal Dictionary<SignedFileContentKey, string> _hashToCollisionIdMap;

        private Telemetry _telemetry;

        private string _tarToolPath;

        private string _pkgToolPath;
        private string _snPath;

        // Add this field to store the list of files to skip 3rd party check
        private readonly HashSet<string> _itemsToSkip3rdPartyCheck;

        // Update the constructor to accept the list of files to skip 3rd party check
        public Configuration(
            string tempDir,
            List<ItemToSign> itemsToSign,
            Dictionary<string, List<SignInfo>> strongNameInfo,
            Dictionary<ExplicitCertificateKey, string> fileSignInfo,
            Dictionary<string, List<SignInfo>> extensionSignInfo,
            Dictionary<string, List<AdditionalCertificateInformation>> additionalCertificateInformation,
            HashSet<string> itemsToSkip3rdPartyCheck,
            string tarToolPath,
            string pkgToolPath,
            string snPath,
            TaskLoggingHelper log,
            bool useHashInExtractionPath = false,
            Telemetry telemetry = null)
        {
            Debug.Assert(tempDir != null);
            Debug.Assert(itemsToSign != null && !itemsToSign.Any(i => i == null));
            Debug.Assert(strongNameInfo != null);
            Debug.Assert(fileSignInfo != null);

            _pathToContainerUnpackingDirectory = Path.Combine(tempDir, "ContainerSigning");
            _useHashInExtractionPath = useHashInExtractionPath;
            _log = log;
            _strongNameInfo = strongNameInfo;
            _fileSignInfo = fileSignInfo;
            _fileExtensionSignInfo = extensionSignInfo;
            _filesToSign = new List<FileSignInfo>();
            _wixPacks = new List<WixPackInfo>();
            _filesToCopy = new List<KeyValuePair<string, string>>();
            _zipDataMap = new Dictionary<SignedFileContentKey, ZipData>();
            _filesByContentKey = new Dictionary<SignedFileContentKey, FileSignInfo>();
            _itemsToSign = itemsToSign;
            _additionalCertificateInformation = additionalCertificateInformation == null ? new() : additionalCertificateInformation;
            _whichPackagesTheFileIsIn = new Dictionary<SignedFileContentKey, HashSet<string>>();
            _errors = new Dictionary<SigningToolErrorCode, HashSet<SignedFileContentKey>>();
            _wixPacks = _itemsToSign.Where(w => WixPackInfo.IsWixPack(w.FullPath))?.Select(s => new WixPackInfo(s.FullPath)).ToList();
            _hashToCollisionIdMap = new Dictionary<SignedFileContentKey, string>();
            _telemetry = telemetry;
            _tarToolPath = tarToolPath;
            _pkgToolPath = pkgToolPath;
            _snPath = snPath;
            _itemsToSkip3rdPartyCheck = itemsToSkip3rdPartyCheck;
        }

        internal BatchSignInput GenerateListOfFiles()
        {
            Stopwatch gatherInfoTime = Stopwatch.StartNew();
            foreach (var itemToSign in _itemsToSign)
            {
                var contentHash = ContentUtil.GetContentHash(itemToSign.FullPath);
                var fileUniqueKey = new SignedFileContentKey(contentHash, Path.GetFileName(itemToSign.FullPath));

                if (!_whichPackagesTheFileIsIn.TryGetValue(fileUniqueKey, out var packages))
                {
                    packages = new HashSet<string>();
                }

                packages.Add(itemToSign.FullPath);

                _whichPackagesTheFileIsIn[fileUniqueKey] = packages;

                PathWithHash pathWithHash = new PathWithHash(itemToSign.FullPath, contentHash);
                TrackFile(pathWithHash, null, itemToSign.CollisionPriorityId);
            }
            gatherInfoTime.Stop();
            if (_telemetry != null)
            {
                _telemetry.AddMetric("Gather file info duration (s)", gatherInfoTime.ElapsedMilliseconds / 1000);
            }

            if (_errors.Any())
            {
                // Iterate over each pair of <error code, unique file identity>. 
                // We can be sure here that the same file won't have the same error code twice.
                foreach (var errorGroup in _errors)
                {
                    switch (errorGroup.Key)
                    {
                        case SigningToolErrorCode.SIGN002:
                            _log.LogError("Could not determine certificate name for signable file(s):");
                            break;
                    }

                    // For each file that had that error
                    foreach (var erroredFile in errorGroup.Value)
                    {
                        _log.LogError($"\tFile: {erroredFile.FileName}");

                        // Get a list of all containers where the file showed up
                        foreach (var containerName in _whichPackagesTheFileIsIn[erroredFile])
                        {
                            _log.LogError($"\t\t{containerName}");
                        }
                    }
                }
            }

            return new BatchSignInput(_filesToSign.ToImmutableArray(), _zipDataMap.ToImmutableDictionary(), _filesToCopy.ToImmutableArray());
        }

        private FileSignInfo TrackFile(PathWithHash file, PathWithHash parentContainer, string collisionPriorityId)
        {
            bool isNested = parentContainer != null;
            _log.LogMessage($"Tracking file '{file.FullPath}' isNested={isNested}");

            // If there's a wixpack in ItemsToSign which corresponds to this file, pass along the path of 
            // the wixpack so we can associate the wixpack with the item
            var wixPack = _wixPacks.SingleOrDefault(w => w.Moniker.Equals(file.FileName, StringComparison.OrdinalIgnoreCase));
            var fileSignInfo = ExtractSignInfo(file, parentContainer, collisionPriorityId, wixPack.FullPath);

            if (_filesByContentKey.TryGetValue(fileSignInfo.FileContentKey, out var existingSignInfo))
            {
                // If we saw this file already we wouldn't call TrackFile unless this is a top-level file.
                Debug.Assert(!isNested);

                // Copy the signed content to the destination path.
                _filesToCopy.Add(new KeyValuePair<string, string>(existingSignInfo.FullPath, file.FullPath));
                return fileSignInfo;
            }

            if (fileSignInfo.IsUnpackableContainer())
            {
                if (fileSignInfo.IsUnpackableWixContainer())
                {
                    _log.LogMessage($"Trying to gather data for wix container {fileSignInfo.FullPath}");
                    if (TryBuildWixData(fileSignInfo, out var msiData))
                    {
                        _zipDataMap[fileSignInfo.FileContentKey] = msiData;
                    }
                    else
                    {
                        _log.LogError($"Failed to build wix data for {fileSignInfo.FullPath}");
                    }
                }
                else
                {
                    if (TryBuildZipData(fileSignInfo, out var zipData))
                    {
                        _zipDataMap[fileSignInfo.FileContentKey] = zipData;
                    }
                    else
                    {
                        _log.LogError($"Failed to build zip data for {fileSignInfo.FullPath}");
                    }
                }
            }
            _log.LogMessage(MessageImportance.Low, $"Caching file {fileSignInfo.FileContentKey.FileName} {fileSignInfo.FileContentKey.StringHash}");
            _filesByContentKey.Add(fileSignInfo.FileContentKey, fileSignInfo);

            bool hasSignableParts = false;
            if (fileSignInfo.IsUnpackableContainer())
            {
                // Only sign containers if the file itself is unsigned, or 
                // an item in the container is unsigned.
                hasSignableParts = _zipDataMap[fileSignInfo.FileContentKey].NestedParts.Values.Any(b => b.FileSignInfo.SignInfo.ShouldSign || b.FileSignInfo.HasSignableParts);
                if(hasSignableParts)
                {
                    // If the file has contents that need to be signed, then re-evaluate the signing info
                    fileSignInfo = fileSignInfo.WithSignableParts();
                    _filesByContentKey[fileSignInfo.FileContentKey] = fileSignInfo;
                }
            }
            if (fileSignInfo.ShouldTrack)
            {
                // We never sign wixpacks
                if (!WixPackInfo.IsWixPack(fileSignInfo.FileName))
                {
                    _filesToSign.Add(fileSignInfo);
                }
            }

            return fileSignInfo;
        }

        /// <summary>
        ///     Determine the file signing info of this file.
        /// </summary>
        /// <param name="fullPath">Full path to the file</param>
        /// <param name="collisionPriorityId">ID used to disambiguate file signing info for nested files.</param>
        /// <param name="contentHash">Content hash of the file</param>
        /// <param name="wixContentFilePath">If a wix container, the corresponding wix pack zip</param>
        /// <param name="parentContainerPath">Path to the parent container. If this is a non-nested container, this should be null</param>
        /// <param name="parentContainerHash">Hash of the parent container. If this is a non-nested container, this should be null</param>
        /// <returns>File signing information for this file.</returns>
        private FileSignInfo ExtractSignInfo(
            PathWithHash file,
            PathWithHash parentContainer,
            string collisionPriorityId,
            string wixContentFilePath)
        {
            var extension = Path.GetExtension(file.FileName);
            string explicitCertificateName = null;
            var fileSpec = string.Empty;
            var isAlreadyAuthenticodeSigned = false;
            var isAlreadyStrongNamed = false;
            var matchedNameTokenFramework = false;
            var matchedNameToken = false;
            var matchedName = false;
            PEInfo peInfo = null;
            SignedFileContentKey signedFileContentKey = new SignedFileContentKey(file.ContentHash, file.FileName);

            // First check for zero length files. These occasionally occur in python and
            // cannot be signed.
            if (file.ContentHash == ContentUtil.EmptyFileContentHash)
            {
                _log.LogMessage(MessageImportance.Low, $"Ignoring zero length file: {file.FullPath}");
                return new FileSignInfo(file, SignInfo.Ignore, wixContentFilePath: wixContentFilePath);
            }

            // handle multi-part extensions like ".symbols.nupkg" specified in FileExtensionSignInfo
            if (_fileExtensionSignInfo != null)
            {
                extension = _fileExtensionSignInfo.OrderByDescending(o => o.Key.Length).FirstOrDefault(f => file.FileName.EndsWith(f.Key, StringComparison.OrdinalIgnoreCase)).Key ?? extension;
            }

            // Asset is nested asset part of a container. Try to get it from the visited assets first
            if (string.IsNullOrEmpty(collisionPriorityId) && parentContainer != null)
            {
                if (!_hashToCollisionIdMap.TryGetValue(signedFileContentKey, out collisionPriorityId))
                {
                    Debug.Assert(parentContainer.FullPath != file.FullPath);

                    // Hash doesn't exist so we use the CollisionPriorityId from the parent container
                    SignedFileContentKey parentSignedFileContentKey =
                        new SignedFileContentKey(parentContainer.ContentHash, parentContainer.FileName);
                    collisionPriorityId = _hashToCollisionIdMap[parentSignedFileContentKey];
                }
            }

            // Update the hash map
            if (!_hashToCollisionIdMap.ContainsKey(signedFileContentKey))
            {
                _hashToCollisionIdMap.Add(signedFileContentKey, collisionPriorityId);
            }
            else
            {
                string existingCollisionId = _hashToCollisionIdMap[signedFileContentKey];

                // If we find that there is an asset which already was processed which has a lower
                // collision id, we use that and update the map so we give it precedence
                if (string.Compare(collisionPriorityId, existingCollisionId) < 0)
                {
                    _hashToCollisionIdMap[signedFileContentKey] = collisionPriorityId;
                }
            }

            // Try to determine default certificate name by the extension of the file. Since there might be dupes
            // we get the one which maps a collision id or the first of the returned ones in case there is no
            // collision id
            bool hasSignInfos = _fileExtensionSignInfo.TryGetValue(extension, out var signInfos);
            SignInfo signInfo = SignInfo.Ignore;
            bool hasSignInfo = false;

            if (hasSignInfos)
            {
                if (!string.IsNullOrEmpty(collisionPriorityId))
                {
                    hasSignInfo = signInfos.Where(s => s.CollisionPriorityId == collisionPriorityId).Any();
                    signInfo = signInfos.Where(s => s.CollisionPriorityId == collisionPriorityId).FirstOrDefault();
                }
                else
                {
                    hasSignInfo = true;
                    signInfo = signInfos.FirstOrDefault();
                }
            }

            if (FileSignInfo.IsPEFile(file.FullPath))
            {
                isAlreadyAuthenticodeSigned = IsSigned(file, VerifySignatures.IsSignedPE(file.FullPath));
                isAlreadyStrongNamed = IsStrongNameSigned(file);

                peInfo = GetPEInfo(file.FullPath);

                if (peInfo.IsManaged && _strongNameInfo.TryGetValue(peInfo.PublicKeyToken, out var pktBasedSignInfos))
                {
                    // Get the default sign info based on the PKT, if applicable. Since there might be dupes
                    // we get the one which maps a collision id or the first of the returned ones in case there is no
                    // collision id
                    SignInfo pktBasedSignInfo = SignInfo.Ignore;

                    if (!string.IsNullOrEmpty(collisionPriorityId))
                    {
                        pktBasedSignInfo = pktBasedSignInfos.Where(s => s.CollisionPriorityId == collisionPriorityId).FirstOrDefault();
                    }
                    else
                    {
                        pktBasedSignInfo = pktBasedSignInfos.FirstOrDefault();
                    }

                    // If crossgenned, we cannot strong name sign this binary.
                    // If the file is already strong name signed, then there is no need to re-sign it. The current signing infra
                    // does not support replacing the SN sig with a new one so if we got here, we can avoid strong name signing
                    // altogether.
                    if (peInfo.IsCrossgened)
                    {
                        signInfo = new SignInfo(pktBasedSignInfo.Certificate, collisionPriorityId: _hashToCollisionIdMap[signedFileContentKey]);
                    }
                    else
                    {
                        signInfo = pktBasedSignInfo.WithIsAlreadyStrongNamed(isAlreadyStrongNamed);
                    }

                    hasSignInfo = true;
                }

                // Check if we have more specific sign info:
                matchedNameTokenFramework = _fileSignInfo.TryGetValue(
                    new ExplicitCertificateKey(file.FileName, peInfo.PublicKeyToken, peInfo.TargetFramework, _hashToCollisionIdMap[signedFileContentKey]),
                    out explicitCertificateName);
                matchedNameToken = !matchedNameTokenFramework && _fileSignInfo.TryGetValue(
                    new ExplicitCertificateKey(file.FileName, peInfo.PublicKeyToken, collisionPriorityId: _hashToCollisionIdMap[signedFileContentKey]),
                    out explicitCertificateName);

                fileSpec = matchedNameTokenFramework ? $" (PublicKeyToken = {peInfo.PublicKeyToken}, Framework = {peInfo.TargetFramework})" :
                        matchedNameToken ? $" (PublicKeyToken = {peInfo.PublicKeyToken})" : string.Empty;
            }
            else if (FileSignInfo.IsPkg(file.FullPath) || FileSignInfo.IsAppBundle(file.FullPath))
            {
                isAlreadyAuthenticodeSigned = IsSigned(file, VerifySignatures.IsSignedPkgOrAppBundle(_log, file.FullPath, _pkgToolPath));
            }
            else if (FileSignInfo.IsNupkg(file.FullPath))
            {
                isAlreadyAuthenticodeSigned = IsSigned(file, VerifySignatures.IsSignedNupkg(file.FullPath));
            }
            else if (FileSignInfo.IsWixInstaller(file.FullPath))
            {
                isAlreadyAuthenticodeSigned = IsSigned(file, VerifySignatures.IsWixSigned(file.FullPath));
            }
            else if (FileSignInfo.IsDeb(file.FullPath))
            {
                isAlreadyAuthenticodeSigned = IsSigned(file, VerifySignatures.IsSignedDeb(_log, file.FullPath));
            }
            else if (FileSignInfo.IsRpm(file.FullPath))
            {
                isAlreadyAuthenticodeSigned = IsSigned(file, VerifySignatures.IsSignedRpm(_log, file.FullPath));;
            }
            else if (FileSignInfo.IsPowerShellScript(file.FullPath))
            {
                isAlreadyAuthenticodeSigned = IsSigned(file, VerifySignatures.IsSignedPowershellFile(file.FullPath));
            }

            // We didn't find any specific information for PE files using PKT + TargetFramework
            if (explicitCertificateName == null)
            {
                matchedName = _fileSignInfo.TryGetValue(new ExplicitCertificateKey(file.FileName,
                    collisionPriorityId: _hashToCollisionIdMap[signedFileContentKey]), out explicitCertificateName);
            }

            // If has overriding info, is it for ignoring the file?
            if (SignToolConstants.IgnoreFileCertificateSentinel.Equals(explicitCertificateName, StringComparison.OrdinalIgnoreCase))
            {
                _log.LogMessage(MessageImportance.Low, $"File configured to not be signed: {file.FullPath}{fileSpec}");
                return new FileSignInfo(file, SignInfo.Ignore);
            }

            // Do we have an explicit certificate after all?
            if (explicitCertificateName != null)
            {
                signInfo = signInfo.WithCertificateName(explicitCertificateName, _hashToCollisionIdMap[signedFileContentKey]);
                hasSignInfo = true;
            }

            if (hasSignInfo)
            {
                bool dualCertsAllowed = false;
                string macSignOperation = null;
                string macNotarizationAppName = null;
                if (signInfo.Certificate != null && _additionalCertificateInformation.TryGetValue(signInfo.Certificate, out var additionalInfo))
                {
                    var additionalCertInfo = additionalInfo.FirstOrDefault(a => string.IsNullOrEmpty(a.CollisionPriorityId) ||
                                                                       a.CollisionPriorityId == _hashToCollisionIdMap[signedFileContentKey]);
                    if (additionalCertInfo != null)
                    {
                        dualCertsAllowed = additionalCertInfo.DualSigningAllowed;
                        macSignOperation = additionalCertInfo.MacSigningOperation;
                        macNotarizationAppName = additionalCertInfo.MacNotarizationAppName;
                    }
                }

                // Tracking issue to remove this warning - https://github.com/dotnet/arcade/issues/15761
                if (signInfo.Certificate != null && signInfo.Certificate.Equals("BreakingSignatureChange", StringComparison.OrdinalIgnoreCase))
                {
                    _log.LogWarning($"Skipping file '{file.FullPath}' because .js files are no longer signed by default. " +
                        "To disable this warning, please explicitly define the FileExtensionSignInfo for the .js extension " +
                        "or set the MSBuild property 'NoSignJS' to 'true'.");
                    return new FileSignInfo(file, SignInfo.Ignore, wixContentFilePath: wixContentFilePath);
                }

                // If the file is already signed and we are not allowed to dual sign, and we are not doing a mac notarization operation,
                // then we should not sign the file.
                if (isAlreadyAuthenticodeSigned && !dualCertsAllowed && string.IsNullOrEmpty(macNotarizationAppName))
                {
                    return new FileSignInfo(file, signInfo.WithIsAlreadySigned(isAlreadyAuthenticodeSigned), wixContentFilePath: wixContentFilePath);
                }

                // If the certificate indicates that the file has a split sign/notarize operation,
                // then replace the certificate with the sign certificate and add the notarization operation.
                if (!string.IsNullOrEmpty(macNotarizationAppName))
                {
                    signInfo = signInfo.WithCertificateName(macSignOperation, _hashToCollisionIdMap[signedFileContentKey]);
                    signInfo = signInfo.WithNotarization(macNotarizationAppName, _hashToCollisionIdMap[signedFileContentKey]);
                }

                Check3rdPartyMicrosoftSignatureMismatch(file, peInfo, signInfo);

                return new FileSignInfo(file, signInfo, (peInfo != null && peInfo.TargetFramework != "") ? peInfo.TargetFramework : null, wixContentFilePath: wixContentFilePath);
            }

            if (SignToolConstants.SignableExtensions.Contains(extension))
            {
                // Extract the relative path inside the package / otherwise just return the full path of the file
                LogError(SigningToolErrorCode.SIGN002, signedFileContentKey);
            }
            else
            {
                _log.LogMessage(MessageImportance.Low, $"Ignoring non-signable file: {file.FullPath}");
            }

            return new FileSignInfo(file, SignInfo.Ignore, wixContentFilePath: wixContentFilePath);

            bool IsSigned(PathWithHash file, SigningStatus signingStatus)
            {
                switch (signingStatus)
                {
                    case SigningStatus.Signed:
                        _log.LogMessage(MessageImportance.Low, $"File '{file.FullPath}' is already signed.");
                        return true;
                    case SigningStatus.NotSigned:
                        _log.LogMessage(MessageImportance.Low, $"File '{file.FullPath}' is not signed.");
                        return false;
                    case SigningStatus.Unknown:
                        _log.LogMessage(MessageImportance.Low, $"File '{file.FullPath}' signing status is unknown, treating as unsigned.");
                        return false;
                    default:
                        throw new Exception($"Unexpected signing status {signingStatus}");
                }
            }

            bool IsStrongNameSigned(PathWithHash file)
            {
                bool isAlreadyStrongNamed = StrongNameHelper.IsSigned(file.FullPath, snPath: _snPath);
                if (!isAlreadyStrongNamed)
                {
                    _log.LogMessage(MessageImportance.Low, $"PE file {file.FullPath} does not have a valid strong name signature.");
                }
                else
                {
                    _log.LogMessage(MessageImportance.Low, $"PE file {file.FullPath} has a valid strong name signature.");
                }

                return isAlreadyStrongNamed;
            }
        }

        /// <summary>
        /// Checks for a mismatch between a 3rd party binary/cert, and a Microsoft binary/cert.
        /// When a binary has a 3rd party copyright, it should get a 3rd party cert. If it's first party,
        /// it should not get a 3rd party cert.
        /// 
        /// Logs a warning or message if there is a mismatch.
        /// </summary>
        /// <param name="file">File to verify</param>
        /// <param name="peInfo">File info</param>
        /// <param name="signInfo">Signing information.</param>
        private void Check3rdPartyMicrosoftSignatureMismatch(PathWithHash file, PEInfo peInfo, SignInfo signInfo)
        {
            if (ShouldSkip3rdPartyCheck(file.FileName))
            {
                return;
            }

            if (signInfo.ShouldSign && peInfo != null)
            {
                bool isMicrosoftLibrary = IsMicrosoftLibrary(peInfo.Copyright);
                bool isMicrosoftCertificate = !IsThirdPartyCertificate(signInfo.Certificate);
                if (isMicrosoftLibrary != isMicrosoftCertificate)
                {
                    string warning;
                    SigningToolErrorCode code;
                    if (isMicrosoftLibrary)
                    {
                        code = SigningToolErrorCode.SIGN001;
                        warning = $"Signing Microsoft library '{file.FullPath}' with 3rd party certificate '{signInfo.Certificate}'. The library is considered Microsoft library due to its copyright: '{peInfo.Copyright}'.";
                    }
                    else
                    {
                        code = SigningToolErrorCode.SIGN004;
                        warning = $"Signing 3rd party library '{file.FullPath}' with Microsoft certificate '{signInfo.Certificate}'. The library is considered 3rd party library due to its copyright: '{peInfo.Copyright}'.";
                    }

                    // https://github.com/dotnet/arcade/issues/10293
                    // Turn the else into a warning (and hoist into the if above) after issue is complete.
                    if (peInfo.IsManaged)
                    {
                        LogWarning(code, warning);
                    }
                    else
                    {
                        _log.LogMessage(MessageImportance.High, $"{code.ToString()}: {warning}");
                    }
                }
            }
        }

        private void LogWarning(SigningToolErrorCode code, string message)
            => _log.LogWarning(subcategory: null, warningCode: code.ToString(), helpKeyword: null, file: null, lineNumber: 0, columnNumber: 0, endLineNumber: 0, endColumnNumber: 0, message: message);

        private void LogError(SigningToolErrorCode code, SignedFileContentKey targetFile)
        {
            if (!_errors.TryGetValue(code, out var filesErrored))
            {
                filesErrored = new HashSet<SignedFileContentKey>();
            }

            filesErrored.Add(targetFile);
            _errors[code] = filesErrored;
        }

        /// <summary>
        /// Determines whether a library is a Microsoft library based on copyright.
        /// Copyright used for binary assets (assemblies and packages) built by Microsoft must be Microsoft copyright.
        /// </summary>
        private static bool IsMicrosoftLibrary(string copyright)
            => copyright != null && copyright.Contains("Microsoft");

        private static bool IsThirdPartyCertificate(string name)
            => name.Equals("3PartyDual", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("3PartySHA2", StringComparison.OrdinalIgnoreCase);

        private static PEInfo GetPEInfo(string fullPath)
        {
            bool isManaged = ContentUtil.IsManaged(fullPath);

            if (!isManaged)
            {
                return new PEInfo(isManaged, GetNativeLegalCopyright(fullPath));
            }

            bool isCrossgened = ContentUtil.IsCrossgened(fullPath);
            string publicKeyToken = ContentUtil.GetPublicKeyToken(fullPath);

            GetManagedTargetFrameworkAndCopyright(fullPath, out string targetFramework, out string copyright);
            return new PEInfo(isManaged, isCrossgened, copyright, publicKeyToken, targetFramework);
        }

        /// <summary>
        /// Retrieves the copyright info from the file version info resource structure.
        /// This is used as a backup method, in cases of non-managed binaries as well as managed
        /// binaries in some cases (crossgen)
        /// </summary>
        /// <param name="filePath"></param>
        /// <returns></returns>
        private static string GetNativeLegalCopyright(string filePath)
        {
            var fileVersionInfo = FileVersionInfo.GetVersionInfo(filePath);
            // Native assets have a space rather than an empty string if there is not a legal copyright available.
            return fileVersionInfo.LegalCopyright?.Trim();
        }

        private static void GetManagedTargetFrameworkAndCopyright(string filePath, out string targetFramework, out string copyright)
        {
            targetFramework = string.Empty;
            copyright = string.Empty;

            using (var stream = File.OpenRead(filePath))
            using (var pereader = new PEReader(stream))
            {
                if (pereader.HasMetadata)
                {
                    var metadataReader = pereader.GetMetadataReader();

                    var assemblyDef = metadataReader.GetAssemblyDefinition();
                    foreach (var attributeHandle in assemblyDef.GetCustomAttributes())
                    {
                        var attribute = metadataReader.GetCustomAttribute(attributeHandle);
                        if (QualifiedNameEquals(metadataReader, attribute, "System.Runtime.Versioning", "TargetFrameworkAttribute"))
                        {
                            targetFramework = new FrameworkName(GetTargetFrameworkAttributeValue(metadataReader, attribute)).FullName;
                        }
                        else if (QualifiedNameEquals(metadataReader, attribute, "System.Reflection", "AssemblyCopyrightAttribute"))
                        {
                            copyright = GetTargetFrameworkAttributeValue(metadataReader, attribute);
                        }
                    }
                }
            }

            // If there is no copyright available, it's possible this was a r2r binary. Get the native info instead.
            if (string.IsNullOrEmpty(copyright))
            {
                copyright = GetNativeLegalCopyright(filePath);
            }
        }

        private static bool QualifiedNameEquals(MetadataReader reader, CustomAttribute attribute, string namespaceName, string typeName)
        {
            bool qualifiedNameEquals(StringHandle nameHandle, StringHandle namespaceHandle)
                => reader.StringComparer.Equals(nameHandle, typeName) && reader.StringComparer.Equals(namespaceHandle, namespaceName);

            var ctorHandle = attribute.Constructor;
            switch (ctorHandle.Kind)
            {
                case HandleKind.MemberReference:
                    var container = reader.GetMemberReference((MemberReferenceHandle)ctorHandle).Parent;
                    switch (container.Kind)
                    {
                        case HandleKind.TypeReference:
                            var containerRef = reader.GetTypeReference((TypeReferenceHandle)container);
                            return qualifiedNameEquals(containerRef.Name, containerRef.Namespace);

                        case HandleKind.TypeDefinition:
                            var containerDef = reader.GetTypeDefinition((TypeDefinitionHandle)container);
                            return qualifiedNameEquals(containerDef.Name, containerDef.Namespace);

                        default:
                            return false;
                    }

                case HandleKind.MethodDefinition:
                    var typeDef = reader.GetTypeDefinition(reader.GetMethodDefinition((MethodDefinitionHandle)ctorHandle).GetDeclaringType());
                    return qualifiedNameEquals(typeDef.Name, typeDef.Namespace);

                default:
                    return false;
            }
        }

        private sealed class DummyCustomAttributeTypeProvider : ICustomAttributeTypeProvider<object>
        {
            public static readonly DummyCustomAttributeTypeProvider Instance = new DummyCustomAttributeTypeProvider();
            public object GetPrimitiveType(PrimitiveTypeCode typeCode) => null;
            public object GetSystemType() => null;
            public object GetSZArrayType(object elementType) => null;
            public object GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => null;
            public object GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => null;
            public object GetTypeFromSerializedName(string name) => null;
            public PrimitiveTypeCode GetUnderlyingEnumType(object type) => default;
            public bool IsSystemType(object type) => false;
        }

        private static string GetTargetFrameworkAttributeValue(MetadataReader reader, CustomAttribute attribute)
        {
            var value = attribute.DecodeValue(DummyCustomAttributeTypeProvider.Instance);
            return (value.FixedArguments.Length == 1) ? value.FixedArguments[0].Value as string : null;
        }

        /// <summary>
        /// Build up the <see cref="ZipData"/> instance for a given zip container. This will also report any consistency
        /// errors found when examining the zip archive.
        /// </summary>
        private bool TryBuildZipData(FileSignInfo zipFileSignInfo, out ZipData zipData, string alternativeArchivePath = null)
        {
            string archivePath = zipFileSignInfo.FullPath;
            if (alternativeArchivePath != null)
            {
                archivePath = alternativeArchivePath;
                Debug.Assert(Path.GetExtension(archivePath) == ".zip");
            }
            else
            {
                Debug.Assert(zipFileSignInfo.IsUnpackableContainer());
            }

            try
            {
                var nestedParts = new Dictionary<string, ZipPart>();

                foreach (var entry in ZipData.ReadEntries(archivePath, _pathToContainerUnpackingDirectory, _tarToolPath, _pkgToolPath))
                {
                    using (entry)
                    {
                        if (entry.ContentHash == null)
                        {
                            // this might be just a pointer to a folder. We skip those.
                            continue;
                        }

                        var fileUniqueKey = new SignedFileContentKey(entry.ContentHash, Path.GetFileName(entry.RelativePath));

                        if (!_whichPackagesTheFileIsIn.TryGetValue(fileUniqueKey, out var packages))
                        {
                            packages = new HashSet<string>();
                        }

                        packages.Add(Path.GetFileName(archivePath));

                        _whichPackagesTheFileIsIn[fileUniqueKey] = packages;

                        // if we already encountered file that has the same content we can reuse its signed version when repackaging the container.
                        var fileName = Path.GetFileName(entry.RelativePath);
                        if (!_filesByContentKey.TryGetValue(fileUniqueKey, out var fileSignInfo))
                        {
                            string extractPathRoot = _useHashInExtractionPath ? fileUniqueKey.StringHash : _filesByContentKey.Count().ToString();
                            string tempPath = Path.Combine(_pathToContainerUnpackingDirectory, extractPathRoot, entry.RelativePath);
                            _log.LogMessage($"Extracting file '{fileName}' from '{archivePath}' to '{tempPath}'.");

                            Directory.CreateDirectory(Path.GetDirectoryName(tempPath));

                            entry.WriteToFile(tempPath);

                            _hashToCollisionIdMap.TryGetValue(fileUniqueKey, out string collisionPriorityId);
                            PathWithHash nestedFile = new PathWithHash(tempPath, entry.ContentHash);
                            fileSignInfo = TrackFile(nestedFile, zipFileSignInfo.File, collisionPriorityId);
                        }

                        if (fileSignInfo.ShouldTrack)
                        {
                            nestedParts.Add(entry.RelativePath, new ZipPart(entry.RelativePath, fileSignInfo));
                        }
                    }
                }

                zipData = new ZipData(zipFileSignInfo, nestedParts.ToImmutableDictionary());

                return true;
            }
            catch (Exception e)
            {
                _log.LogErrorFromException(e, true);
                zipData = null;
                return false;
            }
        }

        /// <summary>
        /// Build up the <see cref="ZipData"/> instance for a given zip container. This will also report any consistency
        /// errors found when examining the zip archive.
        /// </summary>
        private bool TryBuildWixData(FileSignInfo msiFileSignInfo, out ZipData zipData)
        {
            // Treat msi as an archive where the filename is the name of the msi, but its contents are from the corresponding wixpack
            return TryBuildZipData(msiFileSignInfo, out zipData, msiFileSignInfo.WixContentFilePath);
        }

        // Add a helper method to check if a file should skip 3rd party check
        private bool ShouldSkip3rdPartyCheck(string fileName)
        {
            return _itemsToSkip3rdPartyCheck != null && _itemsToSkip3rdPartyCheck.Contains(Path.GetFileName(fileName));
        }
    }
}
