﻿// Copyright (c) 2014 Silicon Studio Corp. (http://siliconstudio.co.jp)
// This file is distributed under GPL v3. See LICENSE.md for details.
using System;
using System.Collections.Generic;

using SiliconStudio.Core.Mathematics;

namespace SiliconStudio.Xenko.Input
{
    internal sealed class GestureRecognizerDrag : GestureRecognizerContMotion
    {
        private GestureConfigDrag ConfigDrag { get { return (GestureConfigDrag)Config; } }

        private Dictionary<int, Vector2> fingerIdsToLowFilteredPos = new Dictionary<int, Vector2>();

        private Vector2 startPosition;

        private Vector2 lastPosition;

        private Vector2 currPosition;

        public GestureRecognizerDrag(GestureConfigDrag config, float screenRatio)
            : base(config, screenRatio)
        {
        }
        
        protected override void InitializeGestureVariables()
        {
            startPosition = ComputeMeanPosition(FingerIdsToLastPos.Values);
            lastPosition = startPosition;
            fingerIdsToLowFilteredPos = new Dictionary<int, Vector2>(FingerIdsToLastPos);
        }

        protected override void UpdateGestureVarsAndPerfomChecks()
        {
            foreach (var id in FingerIdsToLastPos.Keys)
            {
                // check that the drag shape is respected and end the gesture if it is not the case
                if (ConfigDrag.DragShape != GestureShape.Free)
                {
                    var compIndex = ConfigDrag.DragShape == GestureShape.Horizontal ? 1 : 0;
                    if (Math.Abs(FingerIdsToLastPos[id][compIndex] - fingerIdsToLowFilteredPos[id][compIndex]) > ConfigDrag.AllowedErrorMargins[compIndex])
                        HasGestureStarted = false;
                }

                // update the finger low filtered position for the finger
                const float lowFilterCoef = 0.9f;
                fingerIdsToLowFilteredPos[id] = fingerIdsToLowFilteredPos[id] * lowFilterCoef + (1f - lowFilterCoef) * FingerIdsToLastPos[id];
            }

            currPosition = ComputeMeanPosition(FingerIdsToLastPos.Values);
        }

        protected override bool GestureBeginningConditionFulfilled()
        {
            return (currPosition - startPosition).Length() >= ConfigDrag.MinimumDragDistance;
        }

        protected override void AddGestureEventToCurrentList(GestureState state)
        {
            var deltaTrans = currPosition - lastPosition;
            CurrentGestureEvents.Add(new GestureEventDrag(state, ConfigDrag.RequiredNumberOfFingers, ElapsedSinceLast, ElapsedSinceBeginning, ConfigDrag.DragShape,
                                                          NormalizeVector(startPosition), NormalizeVector(currPosition), NormalizeVector(deltaTrans)));

            lastPosition = currPosition;

            base.AddGestureEventToCurrentList(state);
        }
    }
}