// Copyright 2021 The Draco Authors.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//      http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//

using System.IO;
using UnityEngine;
#if UNITY_2020_2_OR_NEWER
using UnityEditor.AssetImporters;
#else
using UnityEditor.Experimental.AssetImporters;
#endif

namespace Draco.Editor {

    [ScriptedImporter(1, "drc")]
    public class DracoImporter : ScriptedImporter {

        public override async void OnImportAsset(AssetImportContext ctx) {
            var dracoData = File.ReadAllBytes(ctx.assetPath);
            var draco = new DracoMeshLoader();
            var mesh = await draco.ConvertDracoMeshToUnity(dracoData, sync: true);
            if (mesh == null) {
                Debug.LogError("Import draco file failed");
                return;
            }
            ctx.AddObjectToAsset("mesh", mesh);
            ctx.SetMainObject(mesh);
        }
    }
}