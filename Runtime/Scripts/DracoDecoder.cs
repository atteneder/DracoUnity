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

using System;
using System.Threading.Tasks;
using UnityEngine;

namespace Draco {

    public class DracoDecoder : MonoBehaviour {

        public DracoDecodeInstance[] instances;

        async void Start() {
            var startTime = Time.realtimeSinceStartup;
            var tasks = new Task[instances.Length];
            for (var i = 0; i < instances.Length; i++) {
                var instance = instances[i];
                tasks[i] = instance.Decode();
            }
            await Task.WhenAll(tasks);
            var time = Time.realtimeSinceStartup - startTime;
            Debug.Log($"Decoded {instances.Length} meshes in {time:0.000} seconds");
        }
    }
}
