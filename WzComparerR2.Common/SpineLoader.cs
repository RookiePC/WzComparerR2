﻿using System;
using System.Collections.Generic;
using System.IO;
using WzComparerR2.WzLib;

namespace WzComparerR2.Common
{
    public static class SpineLoader
    {
        private const string AtlasExtension = ".atlas";
        private const string JsonExtension = ".json";
        private const string SkelExtension = ".skel";

        public static SpineDetectionResult Detect(Wz_Node atlasNode)
        {
            if (atlasNode == null || atlasNode.ParentNode == null)
            {
                return SpineDetectionResult.Failed("AtlasNode or its parent cannot be null.");
            }
            if (!atlasNode.Text.EndsWith(AtlasExtension))
            {
                return SpineDetectionResult.Failed($"AtlasNode name has no suffix {AtlasExtension}.");
            }

            string spineName = atlasNode.Text.Substring(0, atlasNode.Text.Length - AtlasExtension.Length);
            Wz_Node parentNode = atlasNode.ParentNode;
            SkeletonLoadType loadType;
            SpineVersion spineVersion;
            Wz_Node skelNode;

            // find skel node in sibling nodes
            if ((skelNode = parentNode.Nodes[spineName + JsonExtension]) != null)
            {
                loadType = SkeletonLoadType.Json;
            }
            else if ((skelNode = parentNode.Nodes[spineName] ?? parentNode.Nodes[spineName + SkelExtension]) != null)
            {
                loadType = SkeletonLoadType.Binary;
            }
            else
            {
                return SpineDetectionResult.Failed("Failed to find skel node.");
            }

            // resolve uols
            if ((atlasNode = atlasNode.ResolveUol()) == null)
            {
                return SpineDetectionResult.Failed("Failed to resolve uol for atlasNode.");
            }
            if ((skelNode = skelNode.ResolveUol()) == null)
            {
                return SpineDetectionResult.Failed("Failed to resolve uol for skelNode.");
            }

            // check atlas data type
            if (atlasNode.Value is not string)
            {
                return SpineDetectionResult.Failed("AtlasNode does not contain a string value.");
            }

            // inference spine version
            string versionStr = null;
            switch (loadType)
            {
                case SkeletonLoadType.Json when skelNode.Value is string json:
                    versionStr = ReadSpineVersionFromJson(json);
                    break;

                case SkeletonLoadType.Binary when skelNode.Value is Wz_Sound wzSound && wzSound.SoundType == Wz_SoundType.Binary:
                    versionStr = ReadSpineVersionFromBinary(wzSound.WzFile.FileStream, wzSound.Offset, wzSound.DataLength);
                    break;

                case SkeletonLoadType.Binary when skelNode.Value is Wz_RawData wzRawData:
                    versionStr = ReadSpineVersionFromBinary(wzRawData.WzFile.FileStream, wzRawData.Offset, wzRawData.Length);
                    break;
            }

            if (versionStr == null)
            {
                return SpineDetectionResult.Failed($"Failed to read version string from skel {loadType}.");
            }
            if (!Version.TryParse(versionStr, out var version))
            {
                return SpineDetectionResult.Failed($"Failed to parse version '{versionStr}'.");
            }

            switch (version.Major)
            {
                case 2: spineVersion = SpineVersion.V2; break;
                case 4: spineVersion = SpineVersion.V4; break;
                default: return SpineDetectionResult.Failed($"Spine version '{versionStr}' is not supported."); ;
            }
            
            return new SpineDetectionResult
            {
                Success = true,
                ResolvedAtlasNode = atlasNode,
                ResolvedSkelNode = skelNode,
                LoadType = loadType,
                Version = spineVersion,
            };
        }

        private static string ReadSpineVersionFromJson(string jsonText)
        {
            // { "skeleton": { "spine": "2.1.27" } }
            using var sr = new StringReader(jsonText);
            object skelObj = Spine.Json.Deserialize(sr);
            if (skelObj is IDictionary<string, object> jRootDict
                && jRootDict.TryGetValue("skeleton", out var jSkeleton)
                && jSkeleton is IDictionary<string, object> jSkeletonDict
                && jSkeletonDict.TryGetValue("spine", out var jSpine)
                && Version.TryParse(jSpine as string, out var spineVer))
            {
                return jSpine as string;
            }
            return null;
        }

        private static string ReadSpineVersionFromBinary(Stream stream, uint offset, int length)
        {
            /* 
             * v4 format:
             * 00-07 hash
             * 08    version len
             * 09-XX version (len-1 bytes)
             * 
             * v2 format:
             * 00        hash len
             * 01-XX     hash (len-1 bytes)
             * (XX+1)    version len
             * (XX+2)-YY version (len-1 bytes) 
             */

            long oldPos = stream.Position;
            try
            {
                stream.Position = offset;
                // this method can detect version from v4 and pre-v3 file format.
                string version = Spine.SkeletonBinary.GetVersionString(stream);
                return version;
            }
            catch 
            {
                // ignore error;
                return null;
            }
            finally 
            { 
                stream.Position = oldPos;
            }
        }

        public static Spine.V2.SkeletonData LoadSkeletonV2(Wz_Node atlasNode, Spine.V2.TextureLoader textureLoader)
        {
            var detectionResult = Detect(atlasNode);
            if (detectionResult.Success && detectionResult.Version == SpineVersion.V2)
            {
                return LoadSkeletonV2(detectionResult, textureLoader);
            }
            return null;
        }

        public static Spine.V2.SkeletonData LoadSkeletonV2(SpineDetectionResult detectionResult, Spine.V2.TextureLoader textureLoader)
        { 
            using var atlasReader = new StringReader((string)detectionResult.ResolvedAtlasNode.Value);
            var atlas = new Spine.V2.Atlas(atlasReader, "", textureLoader);

            switch (detectionResult.LoadType)
            {
                case SkeletonLoadType.Json:
                    using (var skeletonReader = new StringReader((string)detectionResult.ResolvedSkelNode.Value))
                    {
                        var skeletonJson = new Spine.V2.SkeletonJson(atlas);
                        return skeletonJson.ReadSkeletonData(skeletonReader);
                    }

                case SkeletonLoadType.Binary:
                    FileStream fs;
                    switch (detectionResult.ResolvedSkelNode.Value)
                    {
                        case Wz_Sound wzSound:
                            fs = wzSound.WzFile.FileStream;
                            fs.Position = wzSound.Offset;
                            break;

                        case Wz_RawData rawData:
                            fs = rawData.WzFile.FileStream;
                            fs.Position = rawData.Offset;
                            break;

                        default:
                            return null;
                    }
                    var skeletonBinary = new Spine.V2.SkeletonBinary(atlas);
                    return skeletonBinary.ReadSkeletonData(fs);

                default:
                    return null;
            }
        }

        public static Spine.SkeletonData LoadSkeletonV4(Wz_Node atlasNode, Spine.TextureLoader textureLoader)
        {
            var detectionResult = Detect(atlasNode);
            if (detectionResult.Success && detectionResult.Version == SpineVersion.V4)
            {
                return LoadSkeletonV4(detectionResult, textureLoader);
            }
            return null;
        }

        public static Spine.SkeletonData LoadSkeletonV4(SpineDetectionResult detectionResult, Spine.TextureLoader textureLoader)
        {
            using var atlasReader = new StringReader((string)detectionResult.ResolvedAtlasNode.Value);
            var atlas = new Spine.Atlas(atlasReader, "", textureLoader);

            switch (detectionResult.LoadType)
            {
                case SkeletonLoadType.Json:
                    using (var skeletonReader = new StringReader((string)detectionResult.ResolvedSkelNode.Value))
                    {
                        var skeletonJson = new Spine.SkeletonJson(atlas);
                        return skeletonJson.ReadSkeletonData(skeletonReader);
                    }

                case SkeletonLoadType.Binary:
                    FileStream fs;
                    switch (detectionResult.ResolvedSkelNode.Value)
                    {
                        case Wz_Sound wzSound:
                            fs = wzSound.WzFile.FileStream;
                            fs.Position = wzSound.Offset;
                            break;

                        case Wz_RawData rawData:
                            fs = rawData.WzFile.FileStream;
                            fs.Position = rawData.Offset;
                            break;

                        default:
                            return null;
                    }
                    var skeletonBinary = new Spine.SkeletonBinary(atlas);
                    return skeletonBinary.ReadSkeletonData(fs);

                default:
                    return null;
            }
        }
    }

    public enum SkeletonLoadType
    {
        None = 0,
        Json = 1,
        Binary = 2,
    }

    public enum SpineVersion
    {
        Unknown = 0,
        V2 = 2,
        V4 = 4
    }

    public sealed class SpineDetectionResult
    {
        internal SpineDetectionResult()
        {
        }

        public bool Success { get; internal set; }
        public string ErrorDetail { get; internal set; }
        public Wz_Node ResolvedAtlasNode { get; internal set; }
        public Wz_Node ResolvedSkelNode { get; internal set; }
        public SkeletonLoadType LoadType { get; internal set; }
        public SpineVersion Version { get; internal set; }

        public static SpineDetectionResult Failed(string error = null) => new SpineDetectionResult
        {
            Success = false,
            ErrorDetail = error,
        };
    }
}
