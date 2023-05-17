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

    /// <summary>
    /// Lets you assigns Draco data (in form of a <see cref="TextAsset"/>) to one or more
    /// <see cref="MeshFilter"/> targets and decode them at runtime. 
    /// </summary>
    /// <seealso cref="DracoDecoder"/>
    public class DracoDecodeInstance : ScriptableObject {

        [SerializeField]
        TextAsset dracoAsset;
        
        [SerializeField]
        Bounds bounds;

        [SerializeField]
        List<MeshFilter> targets;
        
        /// <summary>
        /// Decodes the Draco data and assigns it to all targets.
        /// </summary>
        /// <returns>A <see cref="Task"/></returns>
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

        /// <summary>
        /// Sets the Draco data asset and its bounds.
        /// </summary>
        /// <param name="newDracoAsset">Draco data.</param>
        /// <param name="newBounds">Bounds of the decoded Draco mesh.</param>
        public void SetAsset(TextAsset newDracoAsset, Bounds newBounds) {
            dracoAsset = newDracoAsset;
            bounds = newBounds;
        }
        
        /// <summary>
        /// Adds a <see cref="MeshFilter"/> target that the Draco mesh will be assigned to when <see cref="Decode"/> is
        /// invoked.
        /// </summary>
        /// <param name="meshFilter">New target to be added</param>
        public void AddTarget(MeshFilter meshFilter) {
            if (targets == null) targets = new List<MeshFilter>();
            targets.Add(meshFilter);
        }
    }
}
