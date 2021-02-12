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
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine;

namespace Draco {

    public class DracoMeshLoader
    {
        /// <summary>
        /// Decodes a Draco mesh
        /// </summary>
        /// <param name="encodedData">Compressed Draco data</param>
        /// <returns>Unity Mesh or null in case of errors</returns>
        public async Task<Mesh> ConvertDracoMeshToUnity(NativeArray<byte> encodedData) {
            var encodedDataPtr = GetUnsafeReadOnlyIntPtr(encodedData);
            return await ConvertDracoMeshToUnity(encodedDataPtr, encodedData.Length);
        }

        /// <summary>
        /// Decodes a Draco mesh
        /// </summary>
        /// <param name="encodedData">Compressed Draco data</param>
        /// <returns>Unity Mesh or null in case of errors</returns>
        public async Task<Mesh> ConvertDracoMeshToUnity(byte[] encodedData) {
            var encodedDataPtr = PinGCArrayAndGetDataAddress(encodedData, out var gcHandle);
            var result = await ConvertDracoMeshToUnity(encodedDataPtr, encodedData.Length);
            UnsafeUtility.ReleaseGCObject(gcHandle);
            return result;
        }

        async Task<Mesh> ConvertDracoMeshToUnity(IntPtr encodedData, int size) {
            var dracoNative = new DracoNative();
            await WaitForJobHandle(dracoNative.Init(encodedData, size));
            if (dracoNative.ErrorOccured()) {
                return null;
            }
            dracoNative.CreateMesh();
            await WaitForJobHandle(dracoNative.DecodeVertexData());
            var error = dracoNative.ErrorOccured();
            dracoNative.DisposeDracoMesh();
            if (error) {
                return null;
            }
            return dracoNative.PopulateMeshData();
        }

        static async Task WaitForJobHandle(JobHandle jobHandle) {
            while (!jobHandle.IsCompleted) {
                await Task.Yield();
            }
            jobHandle.Complete();
        }
        
        static unsafe IntPtr GetUnsafeReadOnlyIntPtr(NativeArray<byte> encodedData) {
            return (IntPtr) encodedData.GetUnsafeReadOnlyPtr();
        }
        
        static unsafe IntPtr PinGCArrayAndGetDataAddress(byte[] encodedData, out ulong gcHandle) {
            return (IntPtr) UnsafeUtility.PinGCArrayAndGetDataAddress(encodedData, out gcHandle);
        }
    }
}
