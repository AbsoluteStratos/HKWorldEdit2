﻿using AssetsTools.NET;
using AssetsTools.NET.Extra;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;

namespace Assets.Bundler
{
    public class Loader
    {
        const string ver = "2017.4.10f1";
        const int hkweVersion = 1;
        public static void GenerateLevelFiles(string levelPath)
        {
            AssetsManager am = new AssetsManager();
            EditorUtility.DisplayProgressBar("HKEdit", "Loading class database...", 0f);
            am.LoadClassPackage("cldb.dat");
            am.useTemplateFieldCache = true;
            EditorUtility.DisplayProgressBar("HKEdit", "Reading assets files...", 0.5f);
            GenerateLevelFiles(am, am.LoadAssetsFile(levelPath, true));
        }

        public static void GenerateLevelFiles(AssetsManager am, AssetsFileInstance inst)
        {
            EditorUtility.DisplayProgressBar("HKEdit", "Reading files...", 0f);
            am.UpdateDependencies();

            //quicker asset id lookup
            for (int i = 0; i < am.files.Count; i++)
            {
                AssetsFileInstance afi = am.files[i];
                if (i % 100 == 0)
                    EditorUtility.DisplayProgressBar("HKEdit", "Generating QLTs...", (float)i / am.files.Count);
                afi.table.GenerateQuickLookupTree();
            }

            AssetsFile file = inst.file;
            AssetsFileTable table = inst.table;

            string folderName = Path.GetDirectoryName(inst.path);

            List<AssetFileInfoEx> infos = table.pAssetFileInfo.ToList();

            ReferenceCrawler crawler = new ReferenceCrawler(am);
            List<AssetFileInfoEx> initialGameObjects = table.GetAssetsOfType(0x01);
            for (int i = 0; i < initialGameObjects.Count; i++)
            {
                AssetFileInfoEx inf = initialGameObjects[i];
                if (i % 100 == 0)
                    EditorUtility.DisplayProgressBar("HKEdit", "Recursing GameObject dependencies...", (float)i / initialGameObjects.Count);
                crawler.AddReference(new AssetID(inst.path, (long)inf.index), false);
                crawler.FindReferences(inst, inf);
            }
            Dictionary<AssetID, AssetID> glblToLcl = crawler.references;

            List<Type_0D> types = new List<Type_0D>();
            List<string> typeNames = new List<string>();

            Dictionary<string, AssetsFileInstance> fileToInst = am.files.ToDictionary(d => d.path);
            int j = 0;
            foreach (KeyValuePair<AssetID, AssetID> id in glblToLcl)
            {
                if (j % 100 == 0)
                    EditorUtility.DisplayProgressBar("HKEdit", "Rewiring asset pointers...", (float)j / glblToLcl.Count);
                AssetsFileInstance depInst = fileToInst[id.Key.fileName];
                AssetFileInfoEx depInf = depInst.table.getAssetInfo((ulong)id.Key.pathId);

                ClassDatabaseType clType = AssetHelper.FindAssetClassByID(am.classFile, depInf.curFileType);
                string clName = clType.name.GetString(am.classFile);
                if (!typeNames.Contains(clName))
                {
                    Type_0D type0d = C2T5.Cldb2TypeTree(am.classFile, clName);
                    type0d.classId = (int)depInf.curFileType;
                    types.Add(type0d);
                    typeNames.Add(clName);
                }

                crawler.ReplaceReferences(depInst, depInf, id.Value.pathId);
                j++;
            }

            List<Type_0D> assetTypes = new List<Type_0D>()
            {
                C2T5.Cldb2TypeTree(am.classFile, 0x1c),
                C2T5.Cldb2TypeTree(am.classFile, 0x30),
                C2T5.Cldb2TypeTree(am.classFile, 0x53)
            };

            string origFileName = Path.GetFileNameWithoutExtension(inst.path);

            string sceneGuid = CreateMD5(origFileName);
            //string assetGuid = CreateMD5(origFileName + "-data");

            string ExportedScenes = Path.Combine("Assets", "ExportedScenes");
            //circumvents "!BeginsWithCaseInsensitive(file.pathName, AssetDatabase::kAssetsPathWithSlash)' assertion
            string ExportedScenesData = "ExportedScenesData";

            CreateMetaFile(sceneGuid, Path.Combine(ExportedScenes, origFileName + ".unity.meta"));
            //CreateMetaFile(assetGuid, Path.Combine(ExportedScenes, origFileName + "-data.assets.meta"));

            AssetsFile sceneFile = new AssetsFile(new AssetsFileReader(new MemoryStream(BundleCreator.CreateBlankAssets(ver, types))));
            AssetsFile assetFile = new AssetsFile(new AssetsFileReader(new MemoryStream(BundleCreator.CreateBlankAssets(ver, assetTypes))));

            byte[] sceneFileData;
            using (MemoryStream ms = new MemoryStream())
            using (AssetsFileWriter w = new AssetsFileWriter(ms))
            {
                w.bigEndian = false;
                //unity won't load whole assets files by guid, so we have to use hardcoded paths
                sceneFile.dependencies.pDependencies = new AssetsFileDependency[]
                {
                    CreateDependency(ExportedScenesData + "/" + origFileName + "-data.assets")
                };
                sceneFile.dependencies.dependencyCount = 1;
                sceneFile.Write(w, 0, crawler.sceneReplacers.ToArray(), 0);
                sceneFileData = ms.ToArray();
            }
            byte[] assetFileData;
            using (MemoryStream ms = new MemoryStream())
            using (AssetsFileWriter w = new AssetsFileWriter(ms))
            {
                w.bigEndian = false;
                assetFile.Write(w, 0, crawler.assetReplacers.ToArray(), 0);
                assetFileData = ms.ToArray();
            }

            File.WriteAllBytes(Path.Combine(ExportedScenes, origFileName + ".unity"), sceneFileData);
            File.WriteAllBytes(Path.Combine(ExportedScenesData, origFileName + "-data.assets"), assetFileData);

            EditorUtility.ClearProgressBar();
        }

        private static string CreateMD5(string input)
        {
            using (MD5 md5 = MD5.Create())
            {
                byte[] inputBytes = Encoding.UTF8.GetBytes(input);
                byte[] hashBytes = md5.ComputeHash(inputBytes);

                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < hashBytes.Length; i++)
                {
                    sb.Append(hashBytes[i].ToString("x2"));
                }
                return sb.ToString();
            }
        }

        private static void CreateMetaFile(string guid, string path)
        {
            File.WriteAllText(path, @"fileFormatVersion: 2
guid: " + guid + @"
DefaultImporter:
  externalObjects: {}
  userData: 
  assetBundleName: 
  assetBundleVariant: 
");
        }

        private static AssetsFileDependency CreateDependency(string path)
        {
            return new AssetsFileDependency()
            {
                guid = new AssetsFileDependency.GUID128()
                {
                    mostSignificant = 0,
                    leastSignificant = 0
                },
                type = 0,
                assetPath = path,
                bufferedPath = "",
            };
        }

        //doesn't work I guess?
        private static AssetsFileDependency CreateDependencyByGuid(string hash)
        {
            byte[] mostSigBytes = StringToByteArrayFastFlip(hash.Substring(0, 16));
            byte[] leastSigBytes = StringToByteArrayFastFlip(hash.Substring(16, 16));
            long mostSignificant = BitConverter.ToInt64(mostSigBytes, 0);
            long leastSignificant = BitConverter.ToInt64(leastSigBytes, 0);
            return new AssetsFileDependency()
            {
                guid = new AssetsFileDependency.GUID128()
                {
                    mostSignificant = mostSignificant,
                    leastSignificant = leastSignificant
                },
                type = 2, //2 or 3?
                assetPath = "",
                bufferedPath = "",
            };
        }

        private static byte[] StringToByteArrayFastFlip(string hex)
        {
            if (hex.Length % 2 == 1)
                return new byte[16];

            byte[] arr = new byte[hex.Length >> 1];

            for (int i = 0; i < hex.Length >> 1; ++i)
            {
                arr[i] = (byte)((GetHexVal(hex[(i << 1) + 1]) << 4) + (GetHexVal(hex[i << 1])));
            }

            return arr;
        }

        private static int GetHexVal(char hex)
        {
            int val = hex;
            return val - (val < 58 ? 48 : 55);
        }

        public static AssetTypeValueField GetBaseField(AssetsManager am, AssetsFile file, AssetFileInfoEx info)
        {
            AssetTypeInstance ati = am.GetATI(file, info);
            return ati.GetBaseField();
        }

        private static byte[] GetBundleData(string bunPath, int index)
        {
            AssetsFileReader r = new AssetsFileReader(File.Open(bunPath, FileMode.Open, FileAccess.Read, FileShare.Read));
            AssetsBundleFile bun = new AssetsBundleFile();
            bun.Read(r, true);

            //if the bundle doesn't have this section return empty
            if (index >= bun.bundleInf6.dirInf.Length)
                return new byte[0];

            AssetsBundleDirectoryInfo06 dirInf = bun.bundleInf6.dirInf[index];
            int start = (int)(bun.bundleHeader6.GetFileDataOffset() + dirInf.offset);
            int length = (int)dirInf.decompressedSize;
            byte[] data;
            r.BaseStream.Position = start;
            data = r.ReadBytes(length);
            return data;
        }

        private static AssetFileInfoEx FindGameObject(AssetsManager am, AssetsFileInstance inst, string name)
        {
            foreach (AssetFileInfoEx info in inst.table.pAssetFileInfo)
            {
                if (info.curFileType == 0x01)
                {
                    ClassDatabaseType type = AssetHelper.FindAssetClassByID(am.classFile, info.curFileType);
                    string infoName = AssetHelper.GetAssetNameFast(inst.file, am.classFile, info);
                    if (infoName == name)
                    {
                        return info;
                    }
                }
            }
            return null;
        }

        private static void AddScriptDependency(List<AssetsFileDependency> fileDeps, long mostSignificant, long leastSignificant)
        {
            fileDeps.Add(new AssetsFileDependency()
            {
                guid = new AssetsFileDependency.GUID128()
                {
                    mostSignificant = mostSignificant,
                    leastSignificant = leastSignificant
                },
                type = 3,
                assetPath = "",
                bufferedPath = "",
            });
        }

        private static byte[] AddMetadataMonobehaviour(byte[] data, long behaviourId)
        {
            //it seems unity is so broken that after something other than
            //gameobjects are added to the asset list, you can't add any
            //monobehaviour components to gameobjects after it or it crashes
            //anyway, since I'm stuck on this one and I can't really push
            //to the beginning of the list, I'll just put the info onto
            //the first gameobject in the scene

            using (MemoryStream fms = new MemoryStream(data))
            using (AssetsFileReader fr = new AssetsFileReader(fms))
            {
                fr.bigEndian = false;
                int componentSize = fr.ReadInt32();
                List<AssetPPtr> pptrs = new List<AssetPPtr>();
                for (int i = 0; i < componentSize; i++)
                {
                    int fileId = fr.ReadInt32();
                    long pathId = fr.ReadInt64();

                    //this gets rid of assets that have no reference
                    if (!(fileId == 0 && pathId == 0))
                    {
                        pptrs.Add(new AssetPPtr((uint)fileId, (ulong)pathId));
                    }
                }
                //add reference to Metadata mb
                pptrs.Add(new AssetPPtr(0, (ulong)behaviourId));

                int assetLengthMinusCP = (int)(data.Length - 4 - (componentSize * 12));

                using (MemoryStream ms = new MemoryStream())
                using (AssetsFileWriter w = new AssetsFileWriter(ms))
                {
                    w.bigEndian = false;
                    w.Write(pptrs.Count);
                    foreach (AssetPPtr pptr in pptrs)
                    {
                        w.Write(pptr.fileID);
                        w.Write(pptr.pathID);
                    }
                    w.Write(fr.ReadBytes(assetLengthMinusCP));
                    return ms.ToArray();
                }
            }
        }

        /////////////////////////////////////////////////////////
        // nope nothing to see here
        /////////////////////////////////////////////////////////

        private static AssetsReplacer CreateEditDifferMonoBehaviour(long goPid, AssetID origGoPptr, long id, Random rand)
        {
            byte[] data;
            using (MemoryStream ms = new MemoryStream())
            using (AssetsFileWriter w = new AssetsFileWriter(ms))
            {
                w.bigEndian = false;
                w.Write(0);
                w.Write(goPid);
                w.Write(1);
                w.Write(1);
                w.Write((long)11500000);
                w.WriteCountStringInt32("");
                w.Align();

                w.Write(0);
                w.Write(origGoPptr.pathId);
                w.Write(origGoPptr.pathId);
                w.Write(0);
                w.Write(rand.Next());
                data = ms.ToArray();
            }
            return new AssetsReplacerFromMemory(0, (ulong)id, 0x72, 0x0000, data);
        }

        private static AssetsReplacer CreateSceneMetadataMonoBehaviour(long goPid, long id, string sceneName, List<long> usedIds)
        {
            byte[] data;
            using (MemoryStream ms = new MemoryStream())
            using (AssetsFileWriter w = new AssetsFileWriter(ms))
            {
                w.bigEndian = false;
                w.Write(0);
                w.Write(goPid);
                w.Write(1);
                w.Write(2);
                w.Write((long)11500000);
                w.WriteCountStringInt32("");
                w.Align();

                w.WriteCountStringInt32(sceneName);
                w.Align();
                w.Write(usedIds.Count);
                foreach (long usedId in usedIds)
                {
                    w.Write(usedId);
                }
                w.Align();
                w.Write(hkweVersion);
                data = ms.ToArray();
            }
            return new AssetsReplacerFromMemory(0, (ulong)id, 0x72, 0x0001, data);
        }

        private static AssetsReplacer CreateSceneMetadataGameObject(long tfPid, long mbPid, long id)
        {
            byte[] data;
            using (MemoryStream ms = new MemoryStream())
            using (AssetsFileWriter w = new AssetsFileWriter(ms))
            {
                w.bigEndian = false;
                w.Write(2);
                w.Write(0);
                w.Write(tfPid);
                w.Write(0);
                w.Write(mbPid);
                w.Write(0);
                w.Align();
                w.WriteCountStringInt32(" <//Hkwe Scene Metadata//>");
                w.Align();
                w.Write((ushort)0);
                w.Write((byte)1);
                data = ms.ToArray();
            }
            return new AssetsReplacerFromMemory(0, (ulong)id, 0x01, 0xFFFF, data);
        }

        private static AssetsReplacer CreateSceneMetadataTransform(long goPid, long id)
        {
            byte[] data;
            using (MemoryStream ms = new MemoryStream())
            using (AssetsFileWriter w = new AssetsFileWriter(ms))
            {
                w.bigEndian = false;
                w.Write(0);
                w.Write(goPid);
                w.Write(0f);
                w.Write(0f);
                w.Write(0f);
                w.Write(1f);
                w.Write(0f);
                w.Write(0f);
                w.Write(0f);
                w.Write(1f);
                w.Write(1f);
                w.Write(1f);
                w.Write(0);
                w.Write(0);
                w.Write((long)0);
                data = ms.ToArray();
            }
            return new AssetsReplacerFromMemory(0, (ulong)id, 0x04, 0xFFFF, data);
        }

        /////////////////////////////////////////////////////////

        private static void CreateEditDifferTypeTree(ClassDatabaseFile cldb, AssetsFileInstance inst)
        {
            Type_0D type = C2T5.Cldb2TypeTree(cldb, 0x72);
            type.scriptIndex = 0x0000;
            type.unknown1 = Constants.editDifferScriptNEHash[0];
            type.unknown2 = Constants.editDifferScriptNEHash[1];
            type.unknown3 = Constants.editDifferScriptNEHash[2];
            type.unknown4 = Constants.editDifferScriptNEHash[3];

            TypeTreeEditor editor = new TypeTreeEditor(type);
            TypeField_0D baseField = type.pTypeFieldsEx[0];

            editor.AddField(baseField, editor.CreateTypeField("unsigned int", "fileId", 1, 4, 0, false));
            editor.AddField(baseField, editor.CreateTypeField("UInt64", "pathId", 1, 8, 0, false));
            editor.AddField(baseField, editor.CreateTypeField("UInt64", "origPathId", 1, 8, 0, false));
            editor.AddField(baseField, editor.CreateTypeField("UInt8", "newAsset", 1, 1, 0, true));
            editor.AddField(baseField, editor.CreateTypeField("int", "instanceId", 1, 4, 0, false));
            type = editor.SaveType();

            inst.file.typeTree.pTypes_Unity5 = inst.file.typeTree.pTypes_Unity5.Concat(new Type_0D[] { type }).ToArray();
            inst.file.typeTree.fieldCount++;
        }

        private static void CreateSceneMetadataTypeTree(ClassDatabaseFile cldb, AssetsFileInstance inst)
        {
            Type_0D type = C2T5.Cldb2TypeTree(cldb, 0x72);
            type.scriptIndex = 0x0001;
            type.unknown1 = Constants.sceneMetadataScriptNEHash[0];
            type.unknown2 = Constants.sceneMetadataScriptNEHash[1];
            type.unknown3 = Constants.sceneMetadataScriptNEHash[2];
            type.unknown4 = Constants.sceneMetadataScriptNEHash[3];

            TypeTreeEditor editor = new TypeTreeEditor(type);
            TypeField_0D baseField = type.pTypeFieldsEx[0];

            uint sceneName = editor.AddField(baseField, editor.CreateTypeField("string", "sceneName", 1, uint.MaxValue, 0, false, false, Flags.AnyChildUsesAlignBytesFlag));
            uint Array = editor.AddField(editor.type.pTypeFieldsEx[sceneName], editor.CreateTypeField("Array", "Array", 2, uint.MaxValue, 0, true, true, Flags.HideInEditorMask));
            editor.AddField(editor.type.pTypeFieldsEx[Array], editor.CreateTypeField("int", "size", 3, 4, 0, false, false, Flags.HideInEditorMask));
            editor.AddField(editor.type.pTypeFieldsEx[Array], editor.CreateTypeField("char", "data", 3, 1, 0, false, false, Flags.HideInEditorMask));
            uint usedIds = editor.AddField(baseField, editor.CreateTypeField("vector", "usedIds", 1, uint.MaxValue, 0, false, false, Flags.AnyChildUsesAlignBytesFlag));
            uint Array2 = editor.AddField(editor.type.pTypeFieldsEx[usedIds], editor.CreateTypeField("Array", "Array", 2, uint.MaxValue, 0, true, true));
            editor.AddField(editor.type.pTypeFieldsEx[Array2], editor.CreateTypeField("int", "size", 3, 4, 0, false));
            editor.AddField(editor.type.pTypeFieldsEx[Array2], editor.CreateTypeField("SInt64", "data", 3, 8, 0, false));
            editor.AddField(baseField, editor.CreateTypeField("int", "hkweVersion", 1, 4, 0, false));
            type = editor.SaveType();

            inst.file.typeTree.pTypes_Unity5 = inst.file.typeTree.pTypes_Unity5.Concat(new Type_0D[] { type }).ToArray();
            inst.file.typeTree.fieldCount++;
        }
    }
}
