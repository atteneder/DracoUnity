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

using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace Draco {

    public class DracoDecodeInstance : ScriptableObject {

        [SerializeField]
        TextAsset dracoAsset;
        
        [SerializeField]
        Bounds bounds;

        [SerializeField]
        List<MeshFilter> targets;
        
        public async Task Decode() {
            var draco = new DracoMeshLoader(false);
            var mesh = await draco.ConvertDracoMeshToUnity(dracoAsset.bytes);
            mesh.bounds = bounds;
    #if DEBUG
            mesh.name = dracoAsset.name;
    #endif
            foreach (var meshFilter in targets) {
                meshFilter.mesh = mesh;
            }
        }

        public void SetAsset(TextAsset dracoAsset, Bounds bounds) {
            this.dracoAsset = dracoAsset;
            this.bounds = bounds;
        }
        
        public void AddTarget(MeshFilter meshFilter) {
            if (targets == null) targets = new List<MeshFilter>();
            targets.Add(meshFilter);
        }
    }
}
