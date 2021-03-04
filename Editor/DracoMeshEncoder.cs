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
using UnityEditor;
using UnityEngine;
using Draco.Encoder;

public static class DracoMeshEncoder
{
    
    [MenuItem("Tools/Draco/Encode Selected Mesh")]
    static void EncodeSelectedMeshes() {
        var meshes = Selection.GetFiltered<Mesh>(SelectionMode.Deep);
        if (meshes == null || meshes.Length <= 0) {
            Debug.LogError("No mesh selected");
            return;
        }
        foreach (var mesh in meshes) {
            EncodeMesh(mesh, Application.streamingAssetsPath);
        }
    }
    
    [MenuItem("Tools/Draco/Test")]
    static void Test() {
        Debug.Log(Selection.activeContext);
        Debug.Log(Selection.activeObject);
    }

    static void EncodeMesh(Mesh mesh, string directory) {
        Debug.Log($"Encode mesh {mesh.name} to {directory}");
        if (!mesh.isReadable) {
            Debug.LogError($"Mesh {mesh.name} is not readable!");
            return;
        }
        var dracoData = DracoEncoder.EncodeMesh(mesh);
        if (dracoData.Length > 1) {
            var filename = string.IsNullOrEmpty(mesh.name) ? "Mesh-submesh-{0}.drc" : $"{mesh.name}-submesh-{{0}}.drc";
            for (var submesh = 0; submesh < dracoData.Length; submesh++) {
                File.WriteAllBytes(Path.Combine(directory,string.Format(filename,submesh)),dracoData[submesh].data.ToArray());
                dracoData[submesh].Dispose();
            }
        }
        else {
            var filename = string.IsNullOrEmpty(mesh.name) ? "Mesh.drc" : $"{mesh.name}.drc";
            File.WriteAllBytes(Path.Combine(directory, filename), dracoData[0].data.ToArray());
            dracoData[0].Dispose();
        }
    }
}
