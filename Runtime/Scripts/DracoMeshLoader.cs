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

// #define DRACO_VERBOSE

using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Events;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;

public unsafe class DracoMeshLoader
{
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
	}

	struct DracoJob : IJob {

		[ReadOnly]
		public NativeArray<byte> data;

		public NativeArray<IntPtr> outMesh;

		public NativeArray<int> result;

		public void Execute() {
			DracoToUnityMesh* tmpMesh;
			result[0] = DecodeMeshForUnity (data.GetUnsafeReadOnlyPtr(), data.Length, &tmpMesh);
			outMesh[0] = (IntPtr) tmpMesh;
		}
	}

	[DllImport ("dracodec_unity")] private static extern int DecodeMeshForUnity (
		void* buffer, int length, DracoToUnityMesh**tmpMesh);

	[DllImport("dracodec_unity")] private static extern int ReleaseUnityMesh(DracoToUnityMesh** tmpMesh);

	private float ReadFloatFromIntPtr (IntPtr data, int offset)
	{
		byte[] byteArray = new byte[4];
		for (int j = 0; j < 4; ++j) {
			byteArray [j] = Marshal.ReadByte (data, offset + j);
		}
		return BitConverter.ToSingle (byteArray, 0);
	}

/*
	// TODO(atteneder): bring it back for editor import
	// TODO(zhafang): Add back LoadFromURL.
	public int LoadMeshFromAsset (string assetName, ref List<Mesh> meshes)
	{
		TextAsset asset = Resources.Load (assetName, typeof(TextAsset)) as TextAsset;
		if (asset == null) {
			Debug.LogError ("Didn't load file!");
			return -1;
		}
		byte[] encodedData = asset.bytes;
		Log (encodedData.Length.ToString ());
		if (encodedData.Length == 0) {
			Debug.LogError ("Didn't load encoded data!");
			return -1;
		}
		return DecodeMesh (encodedData, ref meshes);
	}
//*/

	public UnityAction<List<Mesh>> onMeshesLoaded;

	public IEnumerator DecodeMesh(NativeArray<byte> data) {

		var job = new DracoJob();

		job.data = data;
		job.result = new NativeArray<int>(1,Allocator.TempJob);
		job.outMesh = new NativeArray<IntPtr>(1,Allocator.TempJob);

		var jobHandle = job.Schedule();

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

		var meshes = new List<Mesh>();
		CreateMesh(dracoMesh,ref meshes);

		if(onMeshesLoaded!=null) {
			onMeshesLoaded(meshes);
		}
	}

	unsafe int CreateMesh (IntPtr dracoMesh, ref List<Mesh> meshes)
	{
		DracoToUnityMesh* tmpMesh = (DracoToUnityMesh*) dracoMesh;

		Log ("Num indices: " + tmpMesh->numFaces.ToString ());
		Log ("Num vertices: " + tmpMesh->numVertices.ToString ());
		if (tmpMesh->hasNormal)
			Log ("Decoded mesh normals.");
		if (tmpMesh->hasTexcoord)
			Log ("Decoded mesh texcoords.");
		if (tmpMesh->hasColor)
			Log ("Decoded mesh colors.");

		int numFaces = tmpMesh->numFaces;
		int[] newTriangles = new int[tmpMesh->numFaces * 3];
		for (int i = 0; i < tmpMesh->numFaces; ++i) {
			byte* addr = (byte*)tmpMesh->indices + i * 3 * 4;
			newTriangles[i * 3] = *((int*)addr);
			newTriangles[i * 3 + 1] = *((int*)(addr + 4));
			newTriangles[i * 3 + 2] = *((int*)(addr + 8));
		}

		// For floating point numbers, there's no Marshal functions could directly
		// read from the unmanaged data.
		// TODO(zhafang): Find better way to read float numbers.
		Vector3[] newVertices = new Vector3[tmpMesh->numVertices];
		Vector2[] newUVs = new Vector2[0];
		if (tmpMesh->hasTexcoord)
			newUVs = new Vector2[tmpMesh->numVertices];
		Vector3[] newNormals = new Vector3[0];
		if (tmpMesh->hasNormal)
			newNormals = new Vector3[tmpMesh->numVertices];
		Color[] newColors = new Color[0];
		if (tmpMesh->hasColor)
			newColors = new Color[tmpMesh->numVertices];

		byte* posaddr = (byte*)tmpMesh->position;
		byte* normaladdr = (byte*)tmpMesh->normal;
		byte* coloraddr = (byte*)tmpMesh->color;
		byte* uvaddr = (byte*)tmpMesh->texcoord;

		/// TODO(atteneder): check if we can avoid mem copies with new Mesh API (2019.3?)
		/// by converting void* to NativeArray via
		/// NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray
		var newVerticesPtr = UnsafeUtility.AddressOf(ref newVertices[0]);
		UnsafeUtility.MemCpy(newVerticesPtr,posaddr,tmpMesh->numVertices * 12 );

		if (tmpMesh->hasNormal) {
			var newNormalsPtr = UnsafeUtility.AddressOf(ref newNormals[0]);
			UnsafeUtility.MemCpy(newNormalsPtr,normaladdr,tmpMesh->numVertices * 12 );
		}

		if (tmpMesh->hasColor) {
			var newColorsPtr = UnsafeUtility.AddressOf(ref newColors[0]);
			UnsafeUtility.MemCpy(newColorsPtr,coloraddr,tmpMesh->numVertices * 16 );
		}

		if (tmpMesh->hasTexcoord)
		{
			var newUVsPtr = UnsafeUtility.AddressOf(ref newUVs[0]);
			UnsafeUtility.MemCpy(newUVsPtr,uvaddr,tmpMesh->numVertices * 8 );
		}

		ReleaseUnityMesh (&tmpMesh);

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
		mesh.triangles = newTriangles;
		if (newUVs.Length != 0)
			mesh.uv = newUVs;
		if (newNormals.Length != 0) {
			mesh.normals = newNormals;
		} else {
			mesh.RecalculateNormals ();
			Log ("Mesh doesn't have normals, recomputed.");
		}
		if (newColors.Length != 0) {
			mesh.colors = newColors;
		}
		mesh.RecalculateBounds ();
		meshes.Add (mesh);

		return numFaces;
	}

	[System.Diagnostics.Conditional("DRACO_VERBOSE")]
	static void Log(string format, params object[] args) {
		Debug.LogFormat(format,args);
	}
}
