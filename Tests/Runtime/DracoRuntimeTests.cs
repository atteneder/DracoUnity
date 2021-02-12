using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.TestTools;

namespace Draco.Tests
{
    public class DracoRuntimeTests {
        [UnityTest]
        [UseDracoTestFileCase(new[] {
            "https://raw.githubusercontent.com/google/draco/master/testdata/car.drc",
            "https://raw.githubusercontent.com/google/draco/master/testdata/cube_att.obj.edgebreaker.cl10.2.2.drc",
            "https://raw.githubusercontent.com/google/draco/master/testdata/cube_att.obj.edgebreaker.cl4.2.2.drc",
            "https://raw.githubusercontent.com/google/draco/master/testdata/cube_att.obj.sequential.cl3.2.2.drc",
            "https://raw.githubusercontent.com/google/draco/master/testdata/cube_att_sub_o_2.drc",
            "https://raw.githubusercontent.com/google/draco/master/testdata/cube_att_sub_o_no_metadata.drc",
            
            // // Point clouds are not supported
            // "https://raw.githubusercontent.com/google/draco/master/testdata/cube_pc.drc",
            // "https://raw.githubusercontent.com/google/draco/master/testdata/pc_color.drc",
            // "https://raw.githubusercontent.com/google/draco/master/testdata/pc_kd_color.drc",
            // "https://raw.githubusercontent.com/google/draco/master/testdata/point_cloud_no_qp.drc",
            
            // // Legacy versions not supported
            // "https://raw.githubusercontent.com/google/draco/master/testdata/test_nm.obj.edgebreaker.0.10.0.drc",
            // "https://raw.githubusercontent.com/google/draco/master/testdata/test_nm.obj.edgebreaker.0.9.1.drc",
            // "https://raw.githubusercontent.com/google/draco/master/testdata/test_nm.obj.edgebreaker.1.0.0.drc",
            // "https://raw.githubusercontent.com/google/draco/master/testdata/test_nm.obj.edgebreaker.1.1.0.drc",
            "https://raw.githubusercontent.com/google/draco/master/testdata/test_nm.obj.edgebreaker.1.2.0.drc",
            "https://raw.githubusercontent.com/google/draco/master/testdata/test_nm.obj.edgebreaker.cl10.2.2.drc",
            "https://raw.githubusercontent.com/google/draco/master/testdata/test_nm.obj.edgebreaker.cl4.2.2.drc",
            
            // // Legacy versions not supported
            // "https://raw.githubusercontent.com/google/draco/master/testdata/test_nm.obj.sequential.0.10.0.drc",
            // "https://raw.githubusercontent.com/google/draco/master/testdata/test_nm.obj.sequential.0.9.1.drc",
            // "https://raw.githubusercontent.com/google/draco/master/testdata/test_nm.obj.sequential.1.0.0.drc",
            // "https://raw.githubusercontent.com/google/draco/master/testdata/test_nm.obj.sequential.1.1.0.drc",
            
            "https://raw.githubusercontent.com/google/draco/master/testdata/test_nm.obj.sequential.1.2.0.drc",
            "https://raw.githubusercontent.com/google/draco/master/testdata/test_nm.obj.sequential.cl3.2.2.drc",
            
            // // Legacy versions not supported
            // "https://raw.githubusercontent.com/google/draco/master/testdata/test_nm_quant.0.9.0.drc"
        })]
        public IEnumerator LoadDracoOfficialTestData(string url) {
            yield return RunTest(url);
        }

        IEnumerator RunTest(string url) {
            var webRequest = UnityWebRequest.Get(url);
            yield return webRequest.SendWebRequest();
            if(!string.IsNullOrEmpty(webRequest.error)) {
                Debug.LogErrorFormat("Error loading {0}: {1}",url,webRequest.error);
                yield break;
            }

            var data = new NativeArray<byte>(webRequest.downloadHandler.data,Allocator.Persistent);
            var go = new GameObject("DracoBenchmark");

            var task = LoadBatch(1, data);
            while (!task.IsCompleted) {
                yield return null;
            }
            data.Dispose();
        }
        
        async Task LoadBatch(int quantity, NativeArray<byte> data) {

            var tasks = new List<Task<Mesh>>(quantity);
        
            for (var i = 0; i < quantity; i++)
            {
                DracoMeshLoader dracoLoader = new DracoMeshLoader();
                var task = dracoLoader.ConvertDracoMeshToUnity(data);
                tasks.Add(task);
            }

            while (tasks.Count > 0) {
                var task = await Task.WhenAny(tasks);
                tasks.Remove(task);
                var mesh = await task;
                if (mesh == null) {
                    Debug.LogError("Loading mesh failed");
                }
            }
            await Task.Yield();
        }
    }
}
