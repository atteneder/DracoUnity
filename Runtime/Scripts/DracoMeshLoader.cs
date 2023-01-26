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
using UnityEngine.Profiling;
using UnityEngine.Rendering;

namespace Draco {

    public class DracoMeshLoader {
        
        /// <summary>
        /// If true, coordinate space is converted from right-hand (like in glTF) to left-hand (Unity).
        /// </summary>
        bool convertSpace;

        /// <summary>
        /// Create a DracoMeshLoader instance which let's you decode Draco data.
        /// </summary>
        /// <param name="convertSpace">If true, coordinate space is converted from right-hand (like in glTF) to left-hand (Unity).</param>
        public DracoMeshLoader(bool convertSpace = true) {
            this.convertSpace = convertSpace;
        }

        public struct DecodeResult {
            /// <summary>
            /// True if the decoding was successful
            /// </summary>
            public bool success;
            
            /// <summary>
            /// True, if the normals were marked required, but not present in Draco mesh.
            /// They have to get calculated.
            /// </summary>
            public bool calculateNormals;
            
            /// <summary>
            /// If the Draco file contained bone indices and bone weights,
            /// this property is used to carry them over (since MeshData currently
            /// provides no way to apply those values)
            /// </summary>
            public BoneWeightData boneWeightData;
        }
        
        public class BoneWeightData : IDisposable {
            NativeArray<byte> bonesPerVertex;
            NativeArray<BoneWeight1> boneWeights;

            public BoneWeightData(NativeArray<byte> bonesPerVertex, NativeArray<BoneWeight1> boneWeights) {
                this.bonesPerVertex = bonesPerVertex;
                this.boneWeights = boneWeights;
            }

            public void ApplyOnMesh(Mesh mesh) {
                mesh.SetBoneWeights(bonesPerVertex,boneWeights);
            }

            public void Dispose() {
                bonesPerVertex.Dispose();
                boneWeights.Dispose();
            }
        }

        public const MeshUpdateFlags defaultMeshUpdateFlags = MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers | MeshUpdateFlags.DontResetBoneBounds;
        
        /// <summary>
        /// Decodes a Draco mesh
        /// </summary>
        /// <param name="encodedData">Compressed Draco data</param>
        /// /// <param name="requireNormals">If draco does not contain normals and this is set to true, normals are calculated.</param>
        /// <param name="requireTangents">If draco does not contain tangents and this is set to true, tangents and normals are calculated.</param>
        /// <param name="weightsAttributeId">Draco attribute ID that contains bone weights (for skinning)</param>
        /// <param name="jointsAttributeId">Draco attribute ID that contains bone joint indices (for skinning)</param>
        /// <param name="forceUnityLayout">Enforces vertex buffer layout with highest compatibility. Enable this if you want to use blend shapes on the resulting mesh</param>
        /// <returns>Unity Mesh or null in case of errors</returns>
        public async Task<Mesh> ConvertDracoMeshToUnity(
            NativeSlice<byte> encodedData,
            bool requireNormals = false,
            bool requireTangents = false,
            int weightsAttributeId = -1,
            int jointsAttributeId = -1,
            bool forceUnityLayout = false
            )
        {
            var encodedDataPtr = GetUnsafeReadOnlyIntPtr(encodedData);
            var meshDataArray = Mesh.AllocateWritableMeshData(1); 
            var mesh = meshDataArray[0];
            var result = await ConvertDracoMeshToUnity(
                mesh,
                encodedDataPtr,
                encodedData.Length,
                requireNormals,
                requireTangents,
                weightsAttributeId,
                jointsAttributeId,
                forceUnityLayout
                );
            if (!result.success) {
                meshDataArray.Dispose();
                return null;
            }
            var unityMesh = new Mesh();
            Mesh.ApplyAndDisposeWritableMeshData(meshDataArray,unityMesh,defaultMeshUpdateFlags);
            if (result.boneWeightData != null) {
                result.boneWeightData.ApplyOnMesh(unityMesh);
                result.boneWeightData.Dispose();
            }

            if (unityMesh.GetTopology(0) == MeshTopology.Triangles)
            {
                if (result.calculateNormals)
                {
                    unityMesh.RecalculateNormals();
                }
                if (requireTangents)
                {
                    unityMesh.RecalculateTangents();
                }
            }
            return unityMesh;
        }

        /// <summary>
        /// Decodes a Draco mesh
        /// </summary>
        /// <param name="encodedData">Compressed Draco data</param>
        /// <param name="requireNormals">If draco does not contain normals and this is set to true, normals are calculated.</param>
        /// <param name="requireTangents">If draco does not contain tangents and this is set to true, tangents and normals are calculated.</param>
        /// <param name="weightsAttributeId">Draco attribute ID that contains bone weights (for skinning)</param>
        /// <param name="jointsAttributeId">Draco attribute ID that contains bone joint indices (for skinning)</param>
        /// <param name="forceUnityLayout">Enforces vertex buffer layout with highest compatibility. Enable this if you want to use blend shapes on the resulting mesh</param>
        /// <returns>Unity Mesh or null in case of errors</returns>
        public async Task<Mesh> ConvertDracoMeshToUnity(
            byte[] encodedData,
            bool requireNormals = false,
            bool requireTangents = false,
            int weightsAttributeId = -1,
            int jointsAttributeId = -1,
            bool forceUnityLayout = false
#if UNITY_EDITOR
            ,bool sync = false
#endif
        )
        {
            var encodedDataPtr = PinGCArrayAndGetDataAddress(encodedData, out var gcHandle);
            var meshDataArray = Mesh.AllocateWritableMeshData(1);
            var mesh = meshDataArray[0];
            var result = await ConvertDracoMeshToUnity(
                mesh,
                encodedDataPtr,
                encodedData.Length,
                requireNormals,
                requireTangents,
                weightsAttributeId,
                jointsAttributeId,
                forceUnityLayout
#if UNITY_EDITOR
                ,sync
#endif
            );
            UnsafeUtility.ReleaseGCObject(gcHandle);
            if (!result.success) {
                meshDataArray.Dispose();
                return null;
            }
            var unityMesh = new Mesh();
            Mesh.ApplyAndDisposeWritableMeshData(meshDataArray,unityMesh,defaultMeshUpdateFlags);
            if (result.calculateNormals) {
                unityMesh.RecalculateNormals();
            }
            if (requireTangents) {
                unityMesh.RecalculateTangents();
            }
            return unityMesh;
        }

        /// <summary>
        /// Decodes a Draco mesh
        /// </summary>
        /// <param name="mesh">MeshData used to create the mesh</param>
        /// <param name="encodedData">Compressed Draco data</param>
        /// <param name="requireNormals">If draco does not contain normals and this is set to true, normals are calculated.</param>
        /// <param name="requireTangents">If draco does not contain tangents and this is set to true, tangents and normals are calculated.</param>
        /// <param name="weightsAttributeId">Draco attribute ID that contains bone weights (for skinning)</param>
        /// <param name="jointsAttributeId">Draco attribute ID that contains bone joint indices (for skinning)</param>
        /// <param name="forceUnityLayout">Enforces vertex buffer layout with highest compatibility. Enable this if you want to use blend shapes on the resulting mesh</param>
        /// <returns>A DecodeResult</returns>
        public async Task<DecodeResult> ConvertDracoMeshToUnity(
            Mesh.MeshData mesh,
            byte[] encodedData,
            bool requireNormals = false,
            bool requireTangents = false,
            int weightsAttributeId = -1,
            int jointsAttributeId = -1,
            bool forceUnityLayout = false
            )
        {
            var encodedDataPtr = PinGCArrayAndGetDataAddress(encodedData, out var gcHandle);
            var result = await ConvertDracoMeshToUnity(
                mesh,
                encodedDataPtr,
                encodedData.Length,
                requireNormals,
                requireTangents,
                weightsAttributeId,
                jointsAttributeId,
                forceUnityLayout
                );
            UnsafeUtility.ReleaseGCObject(gcHandle);
            return result;
        }
        
        /// <summary>
        /// Decodes a Draco mesh
        /// </summary>
        /// <param name="mesh">MeshData used to create the mesh</param>
        /// <param name="encodedData">Compressed Draco data</param>
        /// <param name="requireNormals">If draco does not contain normals and this is set to true, normals are calculated.</param>
        /// <param name="requireTangents">If draco does not contain tangents and this is set to true, tangents and normals are calculated.</param>
        /// <param name="weightsAttributeId">Draco attribute ID that contains bone weights (for skinning)</param>
        /// <param name="jointsAttributeId">Draco attribute ID that contains bone joint indices (for skinning)</param>
        /// <param name="forceUnityLayout">Enforces vertex buffer layout with highest compatibility. Enable this if you want to use blend shapes on the resulting mesh</param>
        /// <returns>A DecodeResult</returns>
        public async Task<DecodeResult> ConvertDracoMeshToUnity(
            Mesh.MeshData mesh,
            NativeArray<byte> encodedData,
            bool requireNormals = false,
            bool requireTangents = false,
            int weightsAttributeId = -1,
            int jointsAttributeId = -1,
            bool forceUnityLayout = false
#if UNITY_EDITOR
            ,bool sync = false
#endif
            )
        {
            var encodedDataPtr = GetUnsafeReadOnlyIntPtr(encodedData);
            return await ConvertDracoMeshToUnity(
                mesh,
                encodedDataPtr,
                encodedData.Length,
                requireNormals,
                requireTangents,
                weightsAttributeId,
                jointsAttributeId,
                forceUnityLayout
#if UNITY_EDITOR
                ,sync
#endif
            );
        }

        async Task<DecodeResult> ConvertDracoMeshToUnity(
            Mesh.MeshData mesh,
            IntPtr encodedData,
            int size,
            bool requireNormals,
            bool requireTangents,
            int weightsAttributeId = -1,
            int jointsAttributeId = -1,
            bool forceUnityLayout = false
#if UNITY_EDITOR
            ,bool sync = false
#endif
        )
        {
            var dracoNative = new DracoNative(mesh,convertSpace);
            var result = new DecodeResult();

#if UNITY_EDITOR
            if (sync) {
                dracoNative.InitSync(encodedData, size);
            }
            else
#endif
            {
                await WaitForJobHandle(dracoNative.Init(encodedData, size));
            }
            if (dracoNative.ErrorOccured()) {
                return result;
            }
            if (!requireNormals && requireTangents) {
                // Sanity check: We need normals to calculate tangents
                requireNormals = true;
            }
            dracoNative.CreateMesh(
                out result.calculateNormals,
                requireNormals,
                requireTangents,
                weightsAttributeId,
                jointsAttributeId,
                forceUnityLayout
                );
#if UNITY_EDITOR
            if (sync) {
                dracoNative.DecodeVertexDataSync();
            }
            else
#endif
            {
                await WaitForJobHandle(dracoNative.DecodeVertexData());
            }
            var error = dracoNative.ErrorOccured();
            dracoNative.DisposeDracoMesh();
            if (error) {
                return result;
            }

            result.success = dracoNative.PopulateMeshData();
            if (result.success && dracoNative.hasBoneWeightData) {
                result.boneWeightData = new BoneWeightData(dracoNative.bonesPerVertex, dracoNative.boneWeights);
                dracoNative.DisposeBoneWeightData();
            }
            return result;
        }

        static async Task WaitForJobHandle(JobHandle jobHandle) {
            while (!jobHandle.IsCompleted) {
                await Task.Yield();
            }
            jobHandle.Complete();
        }
        
        static unsafe IntPtr GetUnsafeReadOnlyIntPtr(NativeSlice<byte> encodedData) {
            return (IntPtr) encodedData.GetUnsafeReadOnlyPtr();
        }
        
        static unsafe IntPtr PinGCArrayAndGetDataAddress(byte[] encodedData, out ulong gcHandle) {
            return (IntPtr) UnsafeUtility.PinGCArrayAndGetDataAddress(encodedData, out gcHandle);
        }
    }
}
