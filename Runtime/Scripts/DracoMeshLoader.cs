// Copyright 2019 The Draco Authors.
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

// #define DRACO_VERBOSE

using System;
using System.Collections;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Events;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;

public unsafe class DracoMeshLoader
{
	#if UNITY_EDITOR_OSX || UNITY_WEBGL || UNITY_IOS
        public const string DRACODEC_UNITY_LIB = "__Internal";
    #elif UNITY_ANDROID || UNITY_STANDALONE || UNITY_WSA || UNITY_EDITOR
        public const string DRACODEC_UNITY_LIB = "dracodec_unity";
    #endif

	public const Allocator defaultAllocator = Allocator.Persistent;

	// Must stay the order to be consistent with C++ interface.
	[StructLayout (LayoutKind.Sequential)] private struct DracoToUnityMesh
	{
		public int numFaces;
		public IntPtr indices;
		public int numVertices;
		public IntPtr position;
		public bool hasNormal;
		public IntPtr normal;
		public bool hasTexcoord;
		public IntPtr texcoord;
		public bool hasColor;
		public IntPtr color;
		public bool hasWeights;
		public IntPtr weights;
		public bool hasJoints;
		public IntPtr joints;
	}

	public struct DracoJob : IJob {

		[ReadOnly]
		public NativeSlice<byte> data;

		public NativeArray<IntPtr> outMesh;

		public NativeArray<int> result;

		public int weightsId;
		public int jointsId;

		public void Execute() {
			DracoToUnityMesh* tmpMesh;
			result[0] = DecodeMeshForUnity (data.GetUnsafeReadOnlyPtr(), data.Length, &tmpMesh, weightsId, jointsId);
			outMesh[0] = (IntPtr) tmpMesh;
		}
	}

	[DllImport (DRACODEC_UNITY_LIB)] private static extern int DecodeMeshForUnity (
		void* buffer, int length, DracoToUnityMesh**tmpMesh, int weightsId = -1, int jointsId = -1);
	[DllImport(DRACODEC_UNITY_LIB)] private static extern int ReleaseUnityMesh(DracoToUnityMesh** tmpMesh);

	private float ReadFloatFromIntPtr (IntPtr data, int offset)
	{
		byte[] byteArray = new byte[4];
		for (int j = 0; j < 4; ++j) {
			byteArray [j] = Marshal.ReadByte (data, offset + j);
		}
		return BitConverter.ToSingle (byteArray, 0);
	}

	public UnityAction<Mesh> onMeshesLoaded;

	public IEnumerator DecodeMesh(NativeArray<byte> data) {

		Profiler.BeginSample("JobPrepare");
		var job = new DracoJob();

		job.data = data;
		job.result = new NativeArray<int>(1,defaultAllocator);
		job.outMesh = new NativeArray<IntPtr>(1,defaultAllocator);
		job.weightsId = -1;
		job.jointsId = -1;

		var jobHandle = job.Schedule();
		Profiler.EndSample();

		while(!jobHandle.IsCompleted) {
			yield return null;
		}
		jobHandle.Complete();

		int result = job.result[0];
		IntPtr dracoMesh = job.outMesh[0];

		job.result.Dispose();
		job.outMesh.Dispose();

		if (result <= 0) {
			Debug.LogError ("Failed: Decoding error.");
			yield break;
		}

		bool hasTexcoords;
		bool hasNormals;
		var mesh = CreateMesh(dracoMesh,out hasNormals, out hasTexcoords);

		if(!hasNormals) {
			mesh.RecalculateNormals();
		}
		if(hasTexcoords) {
			mesh.RecalculateTangents();
		}

		if(onMeshesLoaded!=null) {
			onMeshesLoaded(mesh);
		}
	}

#if UNITY_EDITOR
	/// <summary>
	/// Synchronous (non-threaded) version of DecodeMesh for Edtior purpose.
	/// </summary>
	/// <param name="data">Drace file data</param>
	/// <returns>The Mesh</returns>
	public Mesh DecodeMeshSync(NativeArray<byte> data) {

		Profiler.BeginSample("JobPrepare");
		var job = new DracoJob();

		job.data = data;
		job.result = new NativeArray<int>(1,defaultAllocator);
		job.outMesh = new NativeArray<IntPtr>(1,defaultAllocator);
		job.weightsId = -1;
		job.jointsId = -1;

		job.Run();
		Profiler.EndSample();

		int result = job.result[0];
		IntPtr dracoMesh = job.outMesh[0];

		job.result.Dispose();
		job.outMesh.Dispose();

		if (result <= 0) {
			Debug.LogError ("Failed: Decoding error.");
			return null;
		}

		bool hasTexcoords;
		bool hasNormals;
		var mesh = CreateMesh(dracoMesh, out hasNormals, out hasTexcoords);
		
		if(!hasNormals) {
			mesh.RecalculateNormals();
		}
		if(hasTexcoords) {
			mesh.RecalculateTangents();
		}

		return mesh;
	}
#endif

	public unsafe static Mesh CreateMesh (IntPtr dracoMesh, out bool hasNormals, out bool hasTexcoords)
	{
		Profiler.BeginSample("CreateMesh");
		DracoToUnityMesh* tmpMesh = (DracoToUnityMesh*) dracoMesh;

		Log ("Num indices: " + tmpMesh->numFaces.ToString ());
		Log ("Num vertices: " + tmpMesh->numVertices.ToString ());

		Profiler.BeginSample("CreateMeshAlloc");
		int[] newTriangles = new int[tmpMesh->numFaces * 3];
		Vector3[] newVertices = new Vector3[tmpMesh->numVertices];
		Profiler.EndSample();

		Vector2[] newUVs = null;
		Vector3[] newNormals = null;
		Color[] newColors = null;
		Vector4[] newWeights = null;
		int[] newJoints = null;

		Profiler.BeginSample("CreateMeshIndices");
		byte* indicesSrc = (byte*)tmpMesh->indices;
		var indicesPtr = UnsafeUtility.AddressOf(ref newTriangles[0]);
		UnsafeUtility.MemCpy(indicesPtr,indicesSrc,newTriangles.Length*4);
		Profiler.EndSample();

		Profiler.BeginSample("CreateMeshPositions");
		byte* posaddr = (byte*)tmpMesh->position;
		/// TODO(atteneder): check if we can avoid mem copies with new Mesh API (2019.3?)
		/// by converting void* to NativeArray via
		/// NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray
		var newVerticesPtr = UnsafeUtility.AddressOf(ref newVertices[0]);
		UnsafeUtility.MemCpy(newVerticesPtr,posaddr,tmpMesh->numVertices * 12 );
		Profiler.EndSample();

		hasTexcoords = tmpMesh->hasTexcoord;
		hasNormals = tmpMesh->hasNormal;

		if (hasTexcoords) {
			Profiler.BeginSample("CreateMeshUVs");
			Log ("Decoded mesh texcoords.");
			newUVs = new Vector2[tmpMesh->numVertices];
			byte* uvaddr = (byte*)tmpMesh->texcoord;
			var newUVsPtr = UnsafeUtility.AddressOf(ref newUVs[0]);
			UnsafeUtility.MemCpy(newUVsPtr,uvaddr,tmpMesh->numVertices * 8 );
			Profiler.EndSample();
		}
		if (hasNormals) {
			Profiler.BeginSample("CreateMeshNormals");
			Log ("Decoded mesh normals.");
			newNormals = new Vector3[tmpMesh->numVertices];
			byte* normaladdr = (byte*)tmpMesh->normal;
			var newNormalsPtr = UnsafeUtility.AddressOf(ref newNormals[0]);
			UnsafeUtility.MemCpy(newNormalsPtr,normaladdr,tmpMesh->numVertices * 12 );
			Profiler.EndSample();
		}
		if (tmpMesh->hasColor) {
			Profiler.BeginSample("CreateMeshColors");
			Log ("Decoded mesh colors.");
			newColors = new Color[tmpMesh->numVertices];
			byte* coloraddr = (byte*)tmpMesh->color;
			var newColorsPtr = UnsafeUtility.AddressOf(ref newColors[0]);
			UnsafeUtility.MemCpy(newColorsPtr,coloraddr,tmpMesh->numVertices * 16 );
			Profiler.EndSample();
		}
		if (tmpMesh->hasWeights && tmpMesh->hasJoints) {
			Profiler.BeginSample("CreateWeights");
			Log ("Decoded mesh weights.");
			newWeights = new Vector4[tmpMesh->numVertices];
			byte* weightsAddr = (byte*)tmpMesh->weights;
			var newWeightsPtr = UnsafeUtility.AddressOf(ref newWeights[0]);
			UnsafeUtility.MemCpy(newWeightsPtr,weightsAddr,tmpMesh->numVertices * 16 );
			Profiler.EndSample();

			Profiler.BeginSample("CreateJoints");
			Log ("Decoded mesh joints.");
			newJoints = new int[tmpMesh->numVertices*4];
			byte* jointsAddr = (byte*)tmpMesh->joints;
			var newJointsPtr = UnsafeUtility.AddressOf(ref newJoints[0]);
			UnsafeUtility.MemCpy(newJointsPtr,jointsAddr,tmpMesh->numVertices * 16 );
			Profiler.EndSample();
		}

		Profiler.BeginSample("CreateMeshRelease");
		ReleaseUnityMesh (&tmpMesh);
		Profiler.EndSample();

		Profiler.BeginSample("CreateMeshFeeding");
		Mesh mesh = new Mesh ();
#if UNITY_2017_3_OR_NEWER
		mesh.indexFormat =  (newVertices.Length > System.UInt16.MaxValue)
		? UnityEngine.Rendering.IndexFormat.UInt32
		: UnityEngine.Rendering.IndexFormat.UInt16;
#else
		if(newVertices.Length > System.UInt16.MaxValue) {
			throw new System.Exception("Draco meshes with more than 65535 vertices are only supported from Unity 2017.3 onwards.");
		}
#endif

		mesh.vertices = newVertices;
		mesh.SetTriangles(newTriangles,0,true);
		if (newNormals!=null) {
			mesh.normals = newNormals;
		}
		if (newUVs!=null) {
			mesh.uv = newUVs;
		}
		if (newColors!=null) {
			mesh.colors = newColors;
		}
		if (newJoints!=null && newWeights!=null) {
			BoneWeight[] weights = new BoneWeight[newWeights.Length];

			for (int i = 0; i < weights.Length; i++)
			{
				Tuple<float,int>[] values = new Tuple<float, int>[]{
					new Tuple<float, int>(newWeights[i].x,newJoints[i*4]),
					new Tuple<float, int>(newWeights[i].y,newJoints[i*4+1]),
					new Tuple<float, int>(newWeights[i].z,newJoints[i*4+2]),
					new Tuple<float, int>(newWeights[i].w,newJoints[i*4+3])
				};

				Array.Sort(values, (a,b) => { return b.Item1.CompareTo(a.Item1); } );

				weights[i].boneIndex0 = values[0].Item2;
				weights[i].boneIndex1 = values[1].Item2;
				weights[i].boneIndex2 = values[2].Item2;
				weights[i].boneIndex3 = values[3].Item2;

				weights[i].weight0 = values[0].Item1;
				weights[i].weight1 = values[1].Item1;
				weights[i].weight2 = values[2].Item1;
				weights[i].weight3 = values[3].Item1;
			}
			mesh.boneWeights = weights;
		}
		Profiler.EndSample(); // CreateMeshFeeding
		Profiler.EndSample(); // CreateMesh
		return mesh;
	}

	[System.Diagnostics.Conditional("DRACO_VERBOSE")]
	static void Log(string format, params object[] args) {
		Debug.LogFormat(format,args);
	}
}
