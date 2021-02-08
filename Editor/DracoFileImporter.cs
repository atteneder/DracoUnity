// Copyright 2017 The Draco Authors.
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
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Draco {

  public class DracoFileImporter : AssetPostprocessor {
    static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets,
        string[] movedAssets, string[] movedFromAssetPaths) {
      foreach(string str in importedAssets) {
        // Compressed file must be renamed to ".drc.bytes".
        if (str.IndexOf(".drc.bytes") == -1) {
          return;
        }

        DracoMeshLoader dracoLoader = new DracoMeshLoader();

        // The decoded mesh will be named without ".drc.bytes"
        int length = str.Length - ".drc.bytes".Length - str.LastIndexOf('/') - 1;
        string fileName = str.Substring(str.LastIndexOf('/') + 1, length);

        // var mesh = dracoLoader.LoadMeshFromAsset(fileName + ".drc");
        var absolutePath = Path.Combine(Path.GetDirectoryName(Application.dataPath), str);
        var dracoData = File.ReadAllBytes(absolutePath);
        var mesh = dracoLoader.ConvertDracoMeshToUnity(dracoData);
        if (mesh!=null) {
          var dir = Path.GetDirectoryName(str);
          var dstPath = Path.Combine(dir, fileName + ".asset");
          AssetDatabase.CreateAsset (mesh,dstPath);
          AssetDatabase.SaveAssets ();

          // Also create a Prefab for easy usage.
          GameObject newAsset = new GameObject();
          newAsset.hideFlags = HideFlags.HideInHierarchy;
          newAsset.hideFlags = HideFlags.HideInHierarchy;
          var meshFilter = newAsset.AddComponent<MeshFilter>();
          newAsset.AddComponent<MeshRenderer>();
          meshFilter.mesh = UnityEngine.Object.Instantiate(mesh);
          newAsset.transform.parent = newAsset.transform;
          PrefabUtility.SaveAsPrefabAsset(newAsset, Path.Combine(dir, fileName + ".prefab"));
        } else {
          // TODO: Throw exception?
          Debug.Log("Error: Decoding Draco file failed.");
        }
      }
    }
  }
}