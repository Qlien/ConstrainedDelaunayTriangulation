// Copyright 2021 Alejandro Villalba Avila
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), 
// to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, 
// and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, 
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER 
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS 
// IN THE SOFTWARE.

using Game.Utils.Math;
using System.Collections.Generic;
using UnityEngine;

namespace Game.Utils.Triangulation
{
    /// <summary>
    /// Encapsulates the entire constrained Delaunay triangulation algorithm, according to S. W. Sloan's proposal, and stores the resulting triangulation.
    /// Instantiate this class and call Triangulate to obtain the triangulation of a point cloud.
    /// </summary>
    public unsafe class DelaunayTriangulation
    {
        /// <summary>
        /// Gets the metadata of all the generated triangles.
        /// </summary>
        public DelaunayTriangleSet TriangleSet
        {
            get
            {
                return m_triangleSet;
            }
        }

        // The bin grid used for optimizing the search of triangles that contain a points
        protected PointBinGrid m_grid;

        // The metadata of all the generated triangles
        protected DelaunayTriangleSet m_triangleSet;

        // The stack of adjacent triangles, used when checking for the Delaunay constraint
        protected Stack<int> m_adjacentTriangleStack;

        // Indicates that the index of a vertex, edge or triangle is not defined or was not found
        protected const int NOT_FOUND = -1;

        // Indicates that there is no adjacent triangle
        protected const int NO_ADJACENT_TRIANGLE = -1;

        /// <summary>
        /// Generates the triangulation of a point cloud that fulfills the Delaunay constraint. It allows the creation of holes in the
        /// triangulation, formed by closed polygons that do not overlap each other.
        /// </summary>
        /// <param name="inputPoints">The main point cloud. It must contain, at least, 3 points.</param>
        /// <param name="outputTriangles">The resulting triangulation, after discarding the triangles of the holes, if any. The contents of this list is replaced with the result.</param>
        /// <param name="constrainedEdges">Optional. The list of holes. Each hole must be defined by a closed polygon formed by consecutive points sorted counter-clockwise. 
        /// It does not matter if the polugons are convex or concave. It is preferable that holes lay inside the main point cloud.</param>
        public void Triangulate(List<Vector2> inputPoints, List<Triangle2D> outputTriangles, List<List<Vector2>> constrainedEdges = null)
        {
            // Initialize containers
            outputTriangles.Clear();
            outputTriangles.Capacity = inputPoints.Count - 2;

            if (m_triangleSet == null)
            {
                m_triangleSet = new DelaunayTriangleSet(inputPoints.Count - 2);
            }
            else
            {
                m_triangleSet.Clear();
                m_triangleSet.SetCapacity(inputPoints.Count - 2);
            }

            if(m_adjacentTriangleStack == null)
            {
                m_adjacentTriangleStack = new Stack<int>(inputPoints.Count - 2);
            }
            else
            {
                m_adjacentTriangleStack.Clear();
            }

            // 1: Normalization
            Bounds pointCloudBounds = CalculateBoundsWithLeftBottomCornerAtOrigin(inputPoints);

            List<Vector2> normalizedPoints = new List<Vector2>(inputPoints.Count);
            NormalizePoints(inputPoints, pointCloudBounds, normalizedPoints);

            //DelaunayTriangulation.DrawPoints(normalizedPoints, 30.0f);

            // 2: Addition of points to the space partitioning grid
            Bounds normalizedCloudBounds = CalculateBoundsWithLeftBottomCornerAtOrigin(normalizedPoints);
            m_grid = new PointBinGrid(Mathf.CeilToInt(Mathf.Sqrt(Mathf.Sqrt(inputPoints.Count))), normalizedCloudBounds.size);

            for (int i = 0; i < normalizedPoints.Count; ++i)
            {
                m_grid.AddPoint(normalizedPoints[i]);
            }

            m_grid.DrawGrid(new Color(0.0f, 0.0f, 1.0f, 0.2f), 10.0f);

            // 3: Supertriangle initialization
            Triangle2D supertriangle = new Triangle2D(new Vector2(-100.0f, -100.0f), new Vector2(100.0f, -100.0f), new Vector2(0.0f, 100.0f)); // CCW
            
            m_triangleSet.AddTriangle(supertriangle.p0, supertriangle.p1, supertriangle.p2, NO_ADJACENT_TRIANGLE, NO_ADJACENT_TRIANGLE, NO_ADJACENT_TRIANGLE);

            // 4: Adding points to the Triangle set and Triangulation
            // Points are added one at a time, and points that are close together are inserted together because they are sorted in the grid, 
            // so a later step for finding their containing triangle is faster
            for(int i = 0; i < m_grid.Cells.Length; ++i)
            {
                // If the cell contains a bin with points...
                if(m_grid.Cells[i] != null)
                {
                    // All the points in the bin are added together, one by one
                    for (int j = 0; j < m_grid.Cells[i].Count; ++j)
                    {
                        AddPointToTriangulation(m_grid.Cells[i][j]);
                    }
                }
            }

            List<int> trianglesToRemove = new List<int>();

            // 5: Holes creation (constrained edges)
            if (constrainedEdges != null)
            {
                List<List<int>> constrainedEdgeIndices = new List<List<int>>();

                // Adds the points of all the polygons to the triangulation
                for(int i = 0; i < constrainedEdges.Count; ++i)
                {
                    // 5.1: Normalize
                    List<Vector2> normalizedConstrainedEdges = new List<Vector2>(inputPoints.Count);
                    NormalizePoints(constrainedEdges[i], pointCloudBounds, normalizedConstrainedEdges);

                    List<int> polygonEdgeIndices = new List<int>(normalizedConstrainedEdges.Count);

                    // 5.2: Add the points to the Triangle set
                    for (int j = 0; j < normalizedConstrainedEdges.Count - 0; ++j)
                    {
                        if (normalizedConstrainedEdges[j] == normalizedConstrainedEdges[(j + 1) % normalizedConstrainedEdges.Count])
                        {
                            Debug.LogWarning($"The list of constrained edges contains a zero-length edge (2 consecutive coinciding points, indices {j} and {(j + 1) % normalizedConstrainedEdges.Count}). It will be ignored.");
                            continue;
                        }

                        int addedPointIndex = AddPointToTriangulation(normalizedConstrainedEdges[j]);
                        polygonEdgeIndices.Add(addedPointIndex);

                        Debug.DrawLine(normalizedConstrainedEdges[j], normalizedConstrainedEdges[(j + 1) % normalizedConstrainedEdges.Count], Color.cyan, 5.0f);
                    }

                    constrainedEdgeIndices.Add(polygonEdgeIndices);
                }

                // 5.3: Create the constrained edges
                for(int i = 0; i < constrainedEdgeIndices.Count; ++i)
                {
                    for (int j = 0; j < constrainedEdgeIndices[i].Count - 0; ++j)
                    {
                        AddConstrainedEdgeToTriangulation(constrainedEdgeIndices[i][j], constrainedEdgeIndices[i][(j + 1) % constrainedEdgeIndices[i].Count]);
                    }
                }

                // 5.4: Identify all the triangles in the polygon
                for (int i = 0; i < constrainedEdgeIndices.Count; ++i)
                {
                    m_triangleSet.GetTrianglesInPolygon(constrainedEdgeIndices[i], trianglesToRemove);
                }

                // Remove all the triangles left that are not part of the main cloud
                // TODO: How?
            }

            // 6: Supertriangle removal
            GetSupertriangleTriangles(trianglesToRemove);

            for (int i = 0; i < trianglesToRemove.Count; ++i)
            {
                m_triangleSet.DrawTriangle(trianglesToRemove[i], Color.red);
            }

            // 7: Denormalization
            List<Vector2> denormalizedPoints = new List<Vector2>(m_triangleSet.TriangleCount);
            DenormalizePoints(m_triangleSet.Points, pointCloudBounds, denormalizedPoints);

            // 8: Output filtering
            for(int i = 0; i < m_triangleSet.TriangleCount; ++i)
            {
                bool isTriangleToBeRemoved = false;

                // Is the triangle in the "To Remove" list?
                for (int j = 0; j < trianglesToRemove.Count; ++j)
                {
                    if(trianglesToRemove[j] == i)
                    {
                        trianglesToRemove.RemoveAt(j);
                        isTriangleToBeRemoved = true;
                        break;
                    }
                }

                if(!isTriangleToBeRemoved)
                {
                    DelaunayTriangle triangle = m_triangleSet.GetTriangle(i);
                    outputTriangles.Add(new Triangle2D(denormalizedPoints[triangle.p[0]], denormalizedPoints[triangle.p[1]], denormalizedPoints[triangle.p[2]]));
                }
            }
            
            //m_triangles.LogDump();
        }

        /// <summary>
        /// Adds a point to the triangulation, which implies splitting a triangle into 3 pieces and checking that all triangles still fulfill the Delaunay constraint.
        /// </summary>
        /// <remarks>
        /// If the point coincides in space with an existing point, nothing will be done and the index of the existing point will be returned.
        /// </remarks>
        /// <param name="pointToInsert">The point to add to the triangulation.</param>
        /// <returns>The index of the new point in the triangle set.</returns>
        private int AddPointToTriangulation(Vector2 pointToInsert)
        {
            // Note: Adjacent triangle, opposite to the inserted point, is always at index 1
            // Note 2: Adjacent triangles are stored CCW automatically, their index matches the index of the first vertex in every edge, and it is known that vertices are stored CCW

            // 4.1: Check point existence
            int existingPointIndex = m_triangleSet.GetIndexOfPoint(pointToInsert);

            if (existingPointIndex != NOT_FOUND)
            {
                return existingPointIndex;
            }

            // 4.2: Search containing triangle
            int containingTriangleIndex = m_triangleSet.FindTriangleThatContainsPoint(pointToInsert, m_triangleSet.TriangleCount - 1); // Start at the last added triangle

            DelaunayTriangle containingTriangle = m_triangleSet.GetTriangle(containingTriangleIndex);

            // 4.3: Store the point
            // Inserting a new point into a triangle splits it into 3 pieces, 3 new triangles
            int insertedPoint = m_triangleSet.AddPoint(pointToInsert);

            // 4.4: Create 2 triangles
            DelaunayTriangle newTriangle1 = new DelaunayTriangle(insertedPoint, containingTriangle.p[0], containingTriangle.p[1]);
            newTriangle1.adjacent[0] = NO_ADJACENT_TRIANGLE;
            newTriangle1.adjacent[1] = containingTriangle.adjacent[0];
            newTriangle1.adjacent[2] = containingTriangleIndex;
            int triangle1Index = m_triangleSet.AddTriangle(newTriangle1);

            DelaunayTriangle newTriangle2 = new DelaunayTriangle(insertedPoint, containingTriangle.p[2], containingTriangle.p[0]);
            newTriangle2.adjacent[0] = containingTriangleIndex;
            newTriangle2.adjacent[1] = containingTriangle.adjacent[2];
            newTriangle2.adjacent[2] = NO_ADJACENT_TRIANGLE;
            int triangle2Index = m_triangleSet.AddTriangle(newTriangle2);

            // Sets adjacency between the 2 new triangles
            newTriangle1.adjacent[0] = triangle2Index;
            newTriangle2.adjacent[2] = triangle1Index;
            m_triangleSet.SetTriangleAdjacency(triangle1Index, newTriangle1.adjacent);
            m_triangleSet.SetTriangleAdjacency(triangle2Index, newTriangle2.adjacent);

            // Sets the adjacency of the triangles that were adjacent to the original containing triangle
            if (newTriangle1.adjacent[1] != NO_ADJACENT_TRIANGLE)
            {
                m_triangleSet.ReplaceAdjacent(newTriangle1.adjacent[1], containingTriangleIndex, triangle1Index);
            }

            if (newTriangle2.adjacent[1] != NO_ADJACENT_TRIANGLE)
            {
                m_triangleSet.ReplaceAdjacent(newTriangle2.adjacent[1], containingTriangleIndex, triangle2Index);
            }

            // 4.5: Transform containing triangle into the third
            // Original triangle is transformed into the third triangle after the point has split the containing triangle into 3
            containingTriangle.p[0] = insertedPoint;
            containingTriangle.adjacent[0] = triangle1Index;
            containingTriangle.adjacent[2] = triangle2Index;
            m_triangleSet.ReplaceTriangle(containingTriangleIndex, containingTriangle);

            // 4.6: Add new triangles to a stack
            // Triangles that contain the inserted point are added to the stack for them to be processed by the Delaunay swapping algorithm
            if(containingTriangle.adjacent[1] != NO_ADJACENT_TRIANGLE) // If they do not have an opposite triangle in the outter edge, there is no need to check the Delaunay constraint for it
            {
                m_adjacentTriangleStack.Push(containingTriangleIndex);
            }

            if(newTriangle1.adjacent[1] != NO_ADJACENT_TRIANGLE)
            {
                m_adjacentTriangleStack.Push(triangle1Index);
            }

            if (newTriangle2.adjacent[1] != NO_ADJACENT_TRIANGLE)
            {
                m_adjacentTriangleStack.Push(triangle2Index);
            }

            // 4.7: Check Delaunay constraint
            FulfillDelaunayConstraint(m_adjacentTriangleStack);

            return insertedPoint;
        }

        /// <summary>
        /// Process a stack of triangles checking whether they fulfill the Delaunay constraint with respect to their adjacents, swapping edges if they do not.
        /// The adjacent triangles of the processed triangles are added to the stack too, so the check propagates until they all fulfill the condition.
        /// </summary>
        /// <param name="adjacentTrianglesToProcess">Initial set of triangles to check.</param>
        private void FulfillDelaunayConstraint(Stack<int> adjacentTrianglesToProcess)
        {
            while(adjacentTrianglesToProcess.Count > 0)
            {
                int currentTriangleToSwap = adjacentTrianglesToProcess.Pop();
                DelaunayTriangle triangle = m_triangleSet.GetTriangle(currentTriangleToSwap);

                const int OPPOSITE_TRIANGLE_INDEX = 1;

                if(triangle.adjacent[OPPOSITE_TRIANGLE_INDEX] == NO_ADJACENT_TRIANGLE)
                {
                    continue;
                }

                const int NOT_IN_EDGE_VERTEX_INDEX = 0;
                Vector2 triangleVertexNotInEdge = m_triangleSet.GetPointByIndex(triangle.p[NOT_IN_EDGE_VERTEX_INDEX]);

                DelaunayTriangle oppositeTriangle = m_triangleSet.GetTriangle(triangle.adjacent[OPPOSITE_TRIANGLE_INDEX]);
                Triangle2D oppositeTrianglePoints = m_triangleSet.GetTrianglePoints(triangle.adjacent[OPPOSITE_TRIANGLE_INDEX]);

                if(MathUtils.IsPointInsideCircumcircle(oppositeTrianglePoints.p0, oppositeTrianglePoints.p1, oppositeTrianglePoints.p2, triangleVertexNotInEdge))
                {
                    // Finds the edge of the opposite triangle that is shared with the other triangle, this edge will be swapped
                    int sharedEdgeVertexLocalIndex = 0;
                    
                    for (; sharedEdgeVertexLocalIndex < 3; ++sharedEdgeVertexLocalIndex)
                    {
                        if (oppositeTriangle.adjacent[sharedEdgeVertexLocalIndex] == currentTriangleToSwap)
                        {
                            break;
                        }
                    }

                    // Adds the 2 triangles that were adjacent to the opposite triangle, to be processed too
                    if (oppositeTriangle.adjacent[(sharedEdgeVertexLocalIndex + 1) % 3] != NO_ADJACENT_TRIANGLE)
                    {
                        adjacentTrianglesToProcess.Push(oppositeTriangle.adjacent[(sharedEdgeVertexLocalIndex + 1) % 3]);
                    }

                    if (oppositeTriangle.adjacent[(sharedEdgeVertexLocalIndex + 2) % 3] != NO_ADJACENT_TRIANGLE)
                    {
                        adjacentTrianglesToProcess.Push(oppositeTriangle.adjacent[(sharedEdgeVertexLocalIndex + 2) % 3]);
                    }

                    // 4.8: Swap edges
                    SwapEdges(currentTriangleToSwap, triangle, NOT_IN_EDGE_VERTEX_INDEX, oppositeTriangle, sharedEdgeVertexLocalIndex);
                }
            }
        }

        /// <summary>
        /// Given 2 adjacent triangles, it replaces the shared edge with a new edge that joins both opposite vertices. For example, triangles ABC-CBD would become ADC-ABD.
        /// </summary>
        /// <remarks>
        /// For the main triangle, its shared edge vertex is moved so the new shared edge vertex is 1 position behind / or 2 forward (if it was 1, now the shared edge is 0).
        /// </remarks>
        /// <param name="mainTriangleIndex">The index of the main triangle.</param>
        /// <param name="triangle">Data about the main triangle.</param>
        /// <param name="notInEdgeTriangleVertex">The local index of the vertex that is not in the shared edge, in the main triangle.</param>
        /// <param name="oppositeTriangle">Data about the triangle that opposes the main triangle.</param>
        /// <param name="oppositeTriangleSharedEdgeVertexLocalIndex">The local index of the vertex where the shared edge begins, in the opposite triangle.</param>
        private void SwapEdges(int mainTriangleIndex, DelaunayTriangle mainTriangle, int notInEdgeVertexLocalIndex, DelaunayTriangle oppositeTriangle, int oppositeTriangleSharedEdgeVertexLocalIndex)
        {
            //List<int> debugP = triangle.DebugP;
            //List<int> debugA = triangle.DebugAdjacent;
            //List<int> debugP2 = oppositeTriangle.DebugP;
            //List<int> debugA2 = oppositeTriangle.DebugAdjacent;

            int oppositeVertex = (oppositeTriangleSharedEdgeVertexLocalIndex + 2) % 3;

            //           2 _|_ a
            //       A2 _   |   _
            //       _      |      _
            //   0 _     A1 |         _  c (opposite vertex)
            //       _      |      _
            //          _   |   _
            //       A0   _ |_
            //              |
            //            1    b

            //           2 _|_ 
            //       A2 _       _ A1
            //       _             _
            //   0 _________A0_______ 1
            //   a   _             _  c
            //          _       _
            //             _ _
            //              | b
            //            

            // Only one vertex of each triangle is moved
            int oppositeTriangleIndex = mainTriangle.adjacent[(notInEdgeVertexLocalIndex + 1) % 3];
            mainTriangle.p[(notInEdgeVertexLocalIndex + 1) % 3] = oppositeTriangle.p[oppositeVertex];
            oppositeTriangle.p[oppositeTriangleSharedEdgeVertexLocalIndex] = mainTriangle.p[notInEdgeVertexLocalIndex];
            oppositeTriangle.adjacent[oppositeTriangleSharedEdgeVertexLocalIndex] = mainTriangle.adjacent[notInEdgeVertexLocalIndex];
            mainTriangle.adjacent[notInEdgeVertexLocalIndex] = oppositeTriangleIndex;
            mainTriangle.adjacent[(notInEdgeVertexLocalIndex + 1) % 3] = oppositeTriangle.adjacent[oppositeVertex];
            oppositeTriangle.adjacent[oppositeVertex] = mainTriangleIndex;

            m_triangleSet.ReplaceTriangle(mainTriangleIndex, mainTriangle);
            m_triangleSet.ReplaceTriangle(oppositeTriangleIndex, oppositeTriangle);

            // Adjacent triangles are updated too
            if (mainTriangle.adjacent[(notInEdgeVertexLocalIndex + 1) % 3] != NO_ADJACENT_TRIANGLE)
            {
                m_triangleSet.ReplaceAdjacent(mainTriangle.adjacent[(notInEdgeVertexLocalIndex + 1) % 3], oppositeTriangleIndex, mainTriangleIndex);
            }

            if (oppositeTriangle.adjacent[oppositeTriangleSharedEdgeVertexLocalIndex] != NO_ADJACENT_TRIANGLE)
            {
                m_triangleSet.ReplaceAdjacent(oppositeTriangle.adjacent[oppositeTriangleSharedEdgeVertexLocalIndex], mainTriangleIndex, oppositeTriangleIndex);
            }
        }

        /// <summary>
        /// Adds an edge to the triangulation in such a way that it keeps there even if it form triangles that do not fulfill the Delaunay constraint.
        /// If the edge already exists, nothing will be done.
        /// </summary>
        /// <remarks>
        /// The order in which the vertices of the edges are provided is important, as the edge may be part of a polygon whose vertices are sorted CCW.
        /// </remarks>
        /// <param name="endpointAIndex">The index of the first vertex of the edge, in the existing triangulation.</param>
        /// <param name="endpointBIndex">The index of the second vertex of the edge, in the existing triangulation.</param>
        private void AddConstrainedEdgeToTriangulation(int endpointAIndex, int endpointBIndex)
        {
            // Detects if the edge already exists
            if (m_triangleSet.FindTriangleThatContainsEdge(endpointAIndex, endpointBIndex).TriangleIndex != NOT_FOUND)
            {
                return;
            }

            Vector2 edgeEndpointA = m_triangleSet.GetPointByIndex(endpointAIndex);
            Vector2 edgeEndpointB = m_triangleSet.GetPointByIndex(endpointBIndex);

            // 5.3.1: Search for the triangle that contains the beginning of the new edge
            int triangleContainingA = m_triangleSet.FindTriangleThatContainsLineEndpoint(endpointAIndex, endpointBIndex);


            // 5.3.2: Get all the triangle edges intersected by the constrained edge
            List<DelaunayTriangleEdge> intersectedTriangleEdges = new List<DelaunayTriangleEdge>();
            m_triangleSet.GetIntersectingEdges(edgeEndpointA, edgeEndpointB, triangleContainingA, intersectedTriangleEdges);

            List<DelaunayTriangleEdge> newEdges = new List<DelaunayTriangleEdge>();

            while (intersectedTriangleEdges.Count > 0)
            {
                DelaunayTriangleEdge currentIntersectedTriangleEdge = intersectedTriangleEdges[intersectedTriangleEdges.Count - 1];
                intersectedTriangleEdges.RemoveAt(intersectedTriangleEdges.Count - 1);

                // 5.3.3: Form quadrilaterals and swap intersected edges
                // Deduces the data for both triangles
                currentIntersectedTriangleEdge = m_triangleSet.FindTriangleThatContainsEdge(currentIntersectedTriangleEdge.EdgeVertexA, currentIntersectedTriangleEdge.EdgeVertexB);
                DelaunayTriangle intersectedTriangle = m_triangleSet.GetTriangle(currentIntersectedTriangleEdge.TriangleIndex);
                DelaunayTriangle oppositeTriangle = m_triangleSet.GetTriangle(intersectedTriangle.adjacent[currentIntersectedTriangleEdge.EdgeIndex]);
                Triangle2D trianglePoints = m_triangleSet.GetTrianglePoints(currentIntersectedTriangleEdge.TriangleIndex);

                // Gets the opposite vertex of adjacent triangle, knowing the fisrt vertex of the shared edge
                int oppositeVertex = NOT_FOUND;

                //List<int> debugP = intersectedTriangle.DebugP;
                //List<int> debugA = intersectedTriangle.DebugAdjacent;
                //List<int> debugP2 = oppositeTriangle.DebugP;
                //List<int> debugA2 = oppositeTriangle.DebugAdjacent;

                int oppositeSharedEdgeVertex = NOT_FOUND; // The first vertex in the shared edge of the opposite triangle

                for (int j = 0; j < 3; ++j)
                {
                    if (oppositeTriangle.p[j] == intersectedTriangle.p[(currentIntersectedTriangleEdge.EdgeIndex + 1) % 3]) // Comparing with the endpoint B of the edge, since the edge AB is BA in the adjacent triangle
                    {
                        oppositeVertex = oppositeTriangle.p[(j + 2) % 3];
                        oppositeSharedEdgeVertex = j;
                        break;
                    }
                }

                Vector2 oppositePoint = m_triangleSet.GetPointByIndex(oppositeVertex);

                if (MathUtils.IsQuadrilateralConvex(trianglePoints.p0, trianglePoints.p1, trianglePoints.p2, oppositePoint))
                {
                    // Swap
                    int notInEdgeTriangleVertex = (currentIntersectedTriangleEdge.EdgeIndex + 2) % 3;
                    SwapEdges(currentIntersectedTriangleEdge.TriangleIndex, intersectedTriangle, notInEdgeTriangleVertex, oppositeTriangle, oppositeSharedEdgeVertex);

                    // Refreshes triangle data after swapping
                    intersectedTriangle = m_triangleSet.GetTriangle(currentIntersectedTriangleEdge.TriangleIndex);

                    //oppositeTriangle = m_triangles.GetTriangle(intersectedTriangle.adjacent[(currentIntersectedTriangleEdge.EdgeIndex + 2) % 3]);
                    //debugP = intersectedTriangle.DebugP;
                    //debugA = intersectedTriangle.DebugAdjacent;
                    //debugP2 = oppositeTriangle.DebugP;
                    //debugA2 = oppositeTriangle.DebugAdjacent;

                    // Check new diagonal against the intersecting edge
                    Vector2 intersectionPoint;
                    int newTriangleSharedEdgeVertex = (currentIntersectedTriangleEdge.EdgeIndex + 2) % 3; // Read SwapEdges method to understand the +2
                    Vector2 newTriangleSharedEdgePointA = m_triangleSet.GetPointByIndex(intersectedTriangle.p[newTriangleSharedEdgeVertex]);
                    Vector2 newTriangleSharedEdgePointB = m_triangleSet.GetPointByIndex(intersectedTriangle.p[(newTriangleSharedEdgeVertex  + 1) % 3]);

                    DelaunayTriangleEdge newEdge = new DelaunayTriangleEdge(NOT_FOUND, NOT_FOUND, intersectedTriangle.p[newTriangleSharedEdgeVertex], intersectedTriangle.p[(newTriangleSharedEdgeVertex + 1) % 3]);

                    if (newTriangleSharedEdgePointA != edgeEndpointB && newTriangleSharedEdgePointB != edgeEndpointB && // Watch out! It thinks the line intersects with the edge when an endpoint coincides with a triangle vertex, this problem is avoided thanks to this conditions
                        newTriangleSharedEdgePointA != edgeEndpointA && newTriangleSharedEdgePointB != edgeEndpointA &&
                        MathUtils.InsersectionBetweenLines(edgeEndpointA, edgeEndpointB, newTriangleSharedEdgePointA, newTriangleSharedEdgePointB, out intersectionPoint))
                    {
                        // New triangles edge still intersects with the constrained edge, so it is returned to the list
                        intersectedTriangleEdges.Insert(0, newEdge);
                    }
                    else
                    {
                        newEdges.Add(newEdge);
                    }
                }
                else
                {
                    // Back to the list
                    intersectedTriangleEdges.Insert(0, currentIntersectedTriangleEdge);
                }
            }

            // 5.3.4. Check Delaunay constraint and swap edges
            for (int i = 0; i < newEdges.Count; ++i)
            {
                // Checks if the constrained edge coincides with the new edge
                Vector2 triangleEdgePointA = m_triangleSet.GetPointByIndex(newEdges[i].EdgeVertexA);
                Vector2 triangleEdgePointB = m_triangleSet.GetPointByIndex(newEdges[i].EdgeVertexB);

                if ((triangleEdgePointA == edgeEndpointA && triangleEdgePointB == edgeEndpointB) ||
                    (triangleEdgePointB == edgeEndpointA && triangleEdgePointA == edgeEndpointB))
                {
                    continue;
                }

                // Deduces the data for both triangles
                DelaunayTriangleEdge currentEdge = m_triangleSet.FindTriangleThatContainsEdge(newEdges[i].EdgeVertexA, newEdges[i].EdgeVertexB);
                DelaunayTriangle currentEdgeTriangle = m_triangleSet.GetTriangle(currentEdge.TriangleIndex);
                int triangleVertexNotShared = (currentEdge.EdgeIndex + 2) % 3;
                Vector2 trianglePointNotShared = m_triangleSet.GetPointByIndex(currentEdgeTriangle.p[triangleVertexNotShared]);
                DelaunayTriangle oppositeTriangle = m_triangleSet.GetTriangle(currentEdgeTriangle.adjacent[currentEdge.EdgeIndex]);
                Triangle2D oppositeTrianglePoints = m_triangleSet.GetTrianglePoints(currentEdgeTriangle.adjacent[currentEdge.EdgeIndex]);

                //List<int> debugP = currentEdgeTriangle.DebugP;
                //List<int> debugA = currentEdgeTriangle.DebugAdjacent;
                //List<int> debugP2 = oppositeTriangle.DebugP;
                //List<int> debugA2 = oppositeTriangle.DebugAdjacent;

                if (MathUtils.IsPointInsideCircumcircle(oppositeTrianglePoints.p0, oppositeTrianglePoints.p1, oppositeTrianglePoints.p2, trianglePointNotShared))
                {
                    // Finds the edge of the opposite triangle that is shared with the other triangle, this edge will be swapped
                    int sharedEdgeVertexLocalIndex = 0;

                    for (; sharedEdgeVertexLocalIndex < 3; ++sharedEdgeVertexLocalIndex)
                    {
                        if (oppositeTriangle.adjacent[sharedEdgeVertexLocalIndex] == currentEdge.TriangleIndex)
                        {
                            break;
                        }
                    }

                    // Swap
                    SwapEdges(currentEdge.TriangleIndex, currentEdgeTriangle, triangleVertexNotShared, oppositeTriangle, sharedEdgeVertexLocalIndex);
                }
            }

            //Debug.DrawLine(edgeEndpointA, edgeEndpointB, Color.magenta, 10.0f);
        }

        /// <summary>
        /// Gets all the triangles that contain any of the vertices of the supertriangle.
        /// </summary>
        /// <param name="outputTriangles">The triangles of the supertriangle.</param>
        private void GetSupertriangleTriangles(List<int> outputTriangles)
        {
            for (int i = 0; i < 3; ++i) // Vertices of the supertriangle
            {
                List<int> trianglesThatShareVertex = new List<int>();

                m_triangleSet.GetTrianglesWithVertex(i, trianglesThatShareVertex);

                outputTriangles.AddRange(trianglesThatShareVertex);
            }
        }

        /// <summary>
        /// Calculates the bounds of a point cloud, in such a way that the minimum position becomes the center of the box.
        /// </summary>
        /// <param name="points">The points whose bound is to be calculated.</param>
        /// <returns>The bounds that contains all the points.</returns>
        private Bounds CalculateBoundsWithLeftBottomCornerAtOrigin(List<Vector2> points)
        {
            Vector2 newMin = new Vector2(float.MaxValue, float.MaxValue);
            Vector2 newMax = new Vector2(float.MinValue, float.MinValue);

            for (int i = 0; i < points.Count; ++i)
            {
                if(points[i].x > newMax.x)
                {
                    newMax.x = points[i].x;
                }

                if (points[i].y > newMax.y)
                {
                    newMax.y = points[i].y;
                }

                if (points[i].x < newMin.x)
                {
                    newMin.x = points[i].x;
                }

                if (points[i].y < newMin.y)
                {
                    newMin.y = points[i].y;
                }
            }

            Vector2 size = new Vector2(Mathf.Abs(newMax.x - newMin.x), Mathf.Abs(newMax.y - newMin.y));

            return new Bounds(size * 0.5f + newMin, size);
        }

        /// <summary>
        /// Normalizes a list of points according to a bounding box so all of them lay between the coordinates [0,0] and [1,1], while they conserve their 
        /// relative position with respect to the others.
        /// </summary>
        /// <param name="inputPoints">The input points to normalize.</param>
        /// <param name="bounds">The bounding box in which the normalization is based.</param>
        /// <param name="outputNormalizedPoints">The list where the normalized points will be added. Existing points will not be removed. It must not be null.</param>
        private void NormalizePoints(List<Vector2> inputPoints, Bounds bounds, List<Vector2> outputNormalizedPoints)
        {
            float maximumDimension = Mathf.Max(bounds.size.x, bounds.size.y);

            for(int i = 0; i < inputPoints.Count; ++i)
            {
                outputNormalizedPoints.Add((inputPoints[i] - (Vector2)bounds.min) / maximumDimension);
            }
        }

        /// <summary>
        /// Denormalizes a list of points according to a bounding box so all of them lay between the coordinates determined by such box, while they conserve their 
        /// relative position with respect to the others.
        /// </summary>
        /// <param name="inputPoints">The input points to denormalize. They are expected to be previously normalized.</param>
        /// <param name="bounds">The bounding box in which the denormalization is based.</param>
        /// <param name="outputDenormalizedPoints">The list where the denormalized points will be added. Existing points will not be removed. It must not be null.</param>
        private void DenormalizePoints(List<Vector2> inputPoints, Bounds bounds, List<Vector2> outputDenormalizedPoints)
        {
            float maximumDimension = Mathf.Max(bounds.size.x, bounds.size.y);

            for (int i = 0; i < inputPoints.Count; ++i)
            {
                outputDenormalizedPoints.Add(inputPoints[i] * maximumDimension + (Vector2)bounds.min);
            }
        }

        public static void DrawPoints(List<Vector2> points, float duration)
        {
            for(int i = 0; i < points.Count; ++i)
            {
                Debug.DrawRay(points[i], Vector2.up * 0.2f, Color.red, duration);
                Debug.DrawRay(points[i], Vector2.right * 0.2f, Color.green, duration);
            }
        }
    }
}

