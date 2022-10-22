using System;
using System.Collections.Generic;
using UniGLTF;
using UnityEngine;
using VRM;

namespace DVCompanionTestMod
{
    public class DerailVRM
    {
        public static void LoadShaders(AssetBundle assetBundle)
        {
            Shader[] vrmshaders = assetBundle.LoadAllAssets<Shader>();
            foreach (Shader s in vrmshaders)
            {
                VRMShaderDictionary.VRMShaderDictionary.Add(s.name, s);
            }
        }

        private static readonly Dictionary<string, RuntimeGltfInstance> _rgiDict = new Dictionary<string, RuntimeGltfInstance>();

        public static GameObject CreateVRM(string path)
        {
            if (_rgiDict.TryGetValue(path, out RuntimeGltfInstance rgiCache))
                return GameObject.Instantiate(rgiCache.Root);

            GltfData data;
            try
            {
                data = new AutoGltfFileParser(path).Parse();
            }
            catch (Exception)
            {
                return null;
            }

            VRMData vd = new VRMData(data);
            VRMImporterContext vic = new VRMImporterContext(vd);
            RuntimeGltfInstance rgi = vic.Load();
            rgi.ShowMeshes();
            _rgiDict.Add(path, rgi);

            return GameObject.Instantiate(rgi.Root);
        }
    }
}
