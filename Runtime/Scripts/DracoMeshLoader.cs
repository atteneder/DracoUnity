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

	private struct DecodedMesh
	{
		public int[] indices;
		public Vector3[] vertices;
		public Vector3[] normals;
		public Vector2[] uvs;
		public Color[] colors;
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

	static private int maxNumVerticesPerMesh = 60000;

	// Unity only support maximum 65534 vertices per mesh. So large meshes need
	// to be splitted.
	private void SplitMesh (DecodedMesh mesh, ref List<DecodedMesh> splittedMeshes)
	{
		// Map between new indices on a splitted mesh and old indices on the
		// original mesh.
		int[] newToOldIndexMap = new int[maxNumVerticesPerMesh];

		// Index of the first unprocessed corner.
		int baseCorner = 0;
		int indicesCount = mesh.indices.Length;

		// Map between old indices of the original mesh and indices on the currently
		// processed sub-mesh. Inverse of |newToOldIndexMap|.
		int[] oldToNewIndexMap = new int[indicesCount];
		int[] newIndices = new int[indicesCount];


		// Set mapping between existing vertex indices and new vertex indices to
		// a default value.
		for (int i = 0; i < indicesCount; i++)
		{
			oldToNewIndexMap[i] = -1;
		}

		// Number of added vertices for the currently processed sub-mesh.
		int numAddedVertices = 0;

		// Process all corners (faces) of the original mesh.
		while (baseCorner < indicesCount)
		{
			// Reset the old to new indices map that may have been set by previously
			// processed sub-meshes.
			for (int i = 0; i < numAddedVertices; i++)
			{
				oldToNewIndexMap[newToOldIndexMap[i]] = -1;
			}
			numAddedVertices = 0;

			// Number of processed corners on the current sub-mesh.
			int numProcessedCorners = 0;

			// Local storage for indices added to the new sub-mesh for a currently
			// processed face.
			int[] newlyAddedIndices = new int[3];

			// Sub-mesh processing starts here.
			for (; baseCorner + numProcessedCorners < indicesCount;)
			{
				// Number of vertices that we need to add to the current sub-mesh.
				int verticesAdded = 0;
				for (int i = 0; i < 3; i++)
				{
					if (oldToNewIndexMap[mesh.indices[baseCorner + numProcessedCorners + i]] == -1)
					{
						newlyAddedIndices[verticesAdded] = mesh.indices[baseCorner + numProcessedCorners + i];
						verticesAdded++;
					}
				}

				// If the number of new vertices that we need to add is larger than the
				// allowed limit, we need to stop processing the current sub-mesh.
				// The current face will be processed again for the next sub-mesh.
				if (numAddedVertices + verticesAdded > maxNumVerticesPerMesh)
				{
					break;
				}

				// Update mapping between old an new vertex indices.
				for (int i = 0; i < verticesAdded; i++)
				{
					oldToNewIndexMap[newlyAddedIndices[i]] = numAddedVertices;
					newToOldIndexMap[numAddedVertices] = newlyAddedIndices[i];
					numAddedVertices++;
				}

				for (int i = 0; i < 3; i++)
				{
					newIndices[numProcessedCorners] = oldToNewIndexMap[mesh.indices[baseCorner + numProcessedCorners]];
					numProcessedCorners++;
				}
			}
			// Sub-mesh processing done.
			DecodedMesh subMesh = new DecodedMesh();
			subMesh.indices = new int[numProcessedCorners];
			Array.Copy(newIndices, subMesh.indices, numProcessedCorners);
			subMesh.vertices = new Vector3[numAddedVertices];
			for (int i = 0; i < numAddedVertices; i++)
			{
				subMesh.vertices[i] = mesh.vertices[newToOldIndexMap[i]];
			}
			if (mesh.normals != null)
			{
				subMesh.normals = new Vector3[numAddedVertices];
				for (int i = 0; i < numAddedVertices; i++)
				{
					subMesh.normals[i] = mesh.normals[newToOldIndexMap[i]];
				}
			}

			if (mesh.colors != null)
			{
				subMesh.colors = new Color[numAddedVertices];
				for (int i = 0; i < numAddedVertices; i++)
				{
					subMesh.colors[i] = mesh.colors[newToOldIndexMap[i]];
				}
			}

			if (mesh.uvs != null)
			{
				subMesh.uvs = new Vector2[numAddedVertices];
				for (int i = 0; i < numAddedVertices; i++)
				{
					subMesh.uvs[i] = mesh.uvs[newToOldIndexMap[i]];
				}
			}

			splittedMeshes.Add(subMesh);
			baseCorner += numProcessedCorners;
		}
	}

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

		if (newVertices.Length > maxNumVerticesPerMesh) {
			// Unity only support maximum 65534 vertices per mesh. So large meshes
			// need to be splitted.

			DecodedMesh decodedMesh = new DecodedMesh ();
			decodedMesh.vertices = newVertices;
			decodedMesh.indices = newTriangles;
			if (newUVs.Length != 0)
				decodedMesh.uvs = newUVs;
			if (newNormals.Length != 0)
				decodedMesh.normals = newNormals;
			if (newColors.Length != 0)
				decodedMesh.colors = newColors;
			List<DecodedMesh> splittedMeshes = new List<DecodedMesh> ();

			SplitMesh (decodedMesh, ref splittedMeshes);
			for (int i = 0; i < splittedMeshes.Count; ++i) {
				Mesh mesh = new Mesh ();
				mesh.vertices = splittedMeshes [i].vertices;
				mesh.triangles = splittedMeshes [i].indices;
				if (splittedMeshes [i].uvs != null)
					mesh.uv = splittedMeshes [i].uvs;

				if (splittedMeshes [i].colors != null) {
					mesh.colors = splittedMeshes[i].colors;
				}

				if (splittedMeshes [i].normals != null) {
					mesh.normals = splittedMeshes [i].normals;
				} else {
					Log ("Sub mesh doesn't have normals, recomputed.");
					mesh.RecalculateNormals ();
				}
				mesh.RecalculateBounds ();
				meshes.Add (mesh);
			}
		} else {
			Mesh mesh = new Mesh ();
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

			// Scale and translate the decoded mesh so it would be visible to
			// a new camera's default settings.
			float scale = 0.5f / mesh.bounds.extents.x;
			if (0.5f / mesh.bounds.extents.y < scale)
				scale = 0.5f / mesh.bounds.extents.y;
			if (0.5f / mesh.bounds.extents.z < scale)
				scale = 0.5f / mesh.bounds.extents.z;

			Vector3[] vertices = mesh.vertices;
			int i = 0;
			while (i < vertices.Length) {
				vertices[i] *= scale;
				i++;
			}

			mesh.vertices = vertices;
			mesh.RecalculateBounds ();

			Vector3 translate = mesh.bounds.center;
			translate.x = 0 - mesh.bounds.center.x;
			translate.y = 0 - mesh.bounds.center.y;
			translate.z = 2 - mesh.bounds.center.z;

			i = 0;
			while (i < vertices.Length) {
				vertices[i] += translate;
				i++;
			}
			mesh.vertices = vertices;
			mesh.RecalculateBounds ();
			meshes.Add (mesh);
		}

		return numFaces;
	}

	[System.Diagnostics.Conditional("DRACO_VERBOSE")]
	static void Log(string format, params object[] args) {
		Debug.LogFormat(format,args);
	}
}
