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

using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace Draco.Tests
{
    
    public class DracoRuntimeTests {
        
        const string k_URLPrefix = "https://raw.githubusercontent.com/google/draco/master/testdata/";
        
        [UnityTest]
        [UseDracoTestFileCase(new[] {
            "bunny_gltf.drc",
            "car.drc",
            "cube_att.obj.edgebreaker.cl10.2.2.drc",
            "cube_att.obj.edgebreaker.cl4.2.2.drc",
            "cube_att.obj.sequential.cl3.2.2.drc",
            "cube_att_sub_o_2.drc",
            "cube_att_sub_o_no_metadata.drc",
            "octagon_preserved.drc",
            "pc_kd_color.drc",
            "point_cloud_no_qp.drc",
            "test_nm.obj.edgebreaker.cl10.2.2.drc",
            "test_nm.obj.edgebreaker.cl4.2.2.drc",
            "test_nm.obj.sequential.cl3.2.2.drc",
            
            // // Legacy versions not supported
            // "cube_pc.drc",
            // "pc_color.drc",
            // "test_nm.obj.edgebreaker.0.10.0.drc",
            // "test_nm.obj.edgebreaker.0.9.1.drc",
            // "test_nm.obj.edgebreaker.1.0.0.drc",
            // "test_nm.obj.edgebreaker.1.1.0.drc",
            // "test_nm.obj.sequential.0.10.0.drc",
            // "test_nm.obj.sequential.0.9.1.drc",
            // "test_nm.obj.sequential.1.0.0.drc",
            // "test_nm.obj.sequential.1.1.0.drc",
            // "test_nm_quant.0.9.0.drc",
            
            // // Unknown why it does not work
            // "cube_att.drc",
        })]
        public IEnumerator LoadDracoOfficialTestData(string url) {
            yield return RunTest(k_URLPrefix+url);
        }

        [UnityTest]
        [UseDracoTestFileCase(new[] {
            "bunny_gltf.drc",
            "car.drc",
            "cube_att.obj.edgebreaker.cl10.2.2.drc",
            "cube_att.obj.edgebreaker.cl4.2.2.drc",
            "cube_att.obj.sequential.cl3.2.2.drc",
            "cube_att_sub_o_2.drc",
            "cube_att_sub_o_no_metadata.drc",
            "octagon_preserved.drc",
            "test_nm.obj.edgebreaker.cl10.2.2.drc",
            "test_nm.obj.edgebreaker.cl4.2.2.drc",
            "test_nm.obj.sequential.cl3.2.2.drc",
        })]
        public IEnumerator LoadDracoOfficialTestDataNormals(string url) {
            yield return RunTest(k_URLPrefix+url,true);
        }
        
        [UnityTest]
        [UseDracoTestFileCase(new[] {
            "bunny_gltf.drc",
            "car.drc",
            "cube_att.obj.edgebreaker.cl10.2.2.drc",
            "cube_att.obj.edgebreaker.cl4.2.2.drc",
            "cube_att.obj.sequential.cl3.2.2.drc",
            "cube_att_sub_o_2.drc",
            "cube_att_sub_o_no_metadata.drc",
            "octagon_preserved.drc",
            "test_nm.obj.edgebreaker.cl10.2.2.drc",
            "test_nm.obj.edgebreaker.cl4.2.2.drc",
            "test_nm.obj.sequential.cl3.2.2.drc",
        })]
        public IEnumerator LoadDracoOfficialTestDataNormalsTangents(string url) {
            yield return RunTest(k_URLPrefix+url,true, true);
        }
        
        IEnumerator RunTest(string url, bool requireNormals = false, bool requireTangents = false) {
            var webRequest = UnityWebRequest.Get(url);
            yield return webRequest.SendWebRequest();
            if(!string.IsNullOrEmpty(webRequest.error)) {
                Debug.LogErrorFormat("Error loading {0}: {1}",url,webRequest.error);
                yield break;
            }

            var data = new NativeArray<byte>(webRequest.downloadHandler.data,Allocator.Persistent);
            
            var task = LoadBatch(1, data, requireNormals, requireTangents);
            while (!task.IsCompleted) {
                yield return null;
            }
            Assert.IsNull(task.Exception);
            data.Dispose();
        }
        
        async Task LoadBatch(int quantity, NativeArray<byte> data, bool requireNormals = false, bool requireTangents = false) {

            var tasks = new List<Task<Mesh>>(quantity);
        
            for (var i = 0; i < quantity; i++)
            {
                DracoMeshLoader dracoLoader = new DracoMeshLoader();
                var task = dracoLoader.ConvertDracoMeshToUnity(data,requireNormals,requireTangents);
                tasks.Add(task);
            }

            while (tasks.Count > 0) {
                var task = await Task.WhenAny(tasks);
                tasks.Remove(task);
                var mesh = await task;
                if (mesh == null) {
                    Debug.LogError("Loading mesh failed");
                }
                else {
                    if (requireNormals) {
                        var normals = mesh.normals;
                        Assert.Greater(normals.Length,0);
                    }
                    if (requireTangents) {
                        var tangents = mesh.tangents;
                        Assert.Greater(tangents.Length,0);
                    }
                }
            }
            await Task.Yield();
        }

        [Test]
        public void EncodePointCloud() {
            
            var sphereGo = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            var sphere = sphereGo.GetComponent<MeshFilter>().sharedMesh;
            var vertices = sphere.vertices;
            
            var mesh = new Mesh {
                subMeshCount = 1
            };
            mesh.SetSubMesh(0,new SubMeshDescriptor(0,0,MeshTopology.Points));
            mesh.vertices = vertices;

            var result = Draco.Encoder.DracoEncoder.EncodeMesh(mesh);
            Assert.NotNull(result);
            Assert.AreEqual(1, result.Length);
            Assert.AreEqual(2330, result[0].data.Length);
            
            result[0].Dispose();
        }
    }
}
