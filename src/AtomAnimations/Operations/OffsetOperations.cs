using System;
using System.Collections.Generic;
using System.Linq;
using Leap.Unity;
using UnityEngine;

namespace VamTimeline
{
    public class OffsetOperations
    {
        public const string ChangePivotMode = "Change pivot";
        public const string OffsetMode = "Offset";
        public const string RepositionMode = "Move relative to root";

        private readonly AtomAnimationClip _clip;

        public OffsetOperations(AtomAnimationClip clip)
        {
            _clip = clip;
        }

        public void Apply(Snapshot offsetSnapshot, float from, float to, string offsetMode, float smoothingDuration)
        {
            var useRepositionMode = offsetMode == RepositionMode;
            var pivot = Vector3.zero;
            var positionDelta = Vector3.zero;
            var rotationDelta = Quaternion.identity;

            if (useRepositionMode)
            {
                pivot = offsetSnapshot.previousMainPosition;
                positionDelta = offsetSnapshot.mainController.control.position - offsetSnapshot.previousMainPosition;
                rotationDelta = Quaternion.Inverse(offsetSnapshot.previousMainRotation) * offsetSnapshot.mainController.control.rotation;

                offsetSnapshot.mainController.control.SetPositionAndRotation(offsetSnapshot.previousMainPosition, offsetSnapshot.previousMainRotation);
            }

            foreach (var snap in offsetSnapshot.clipboard.controllers)
            {
                var target = _clip.targetControllers.First(t => t.TargetsSameAs(snap.animatableRef, snap.snapshot.position != null, snap.snapshot.rotation != null));
                if (!target.EnsureParentAvailable(false)) continue;

                var control = snap.animatableRef.controller.control;
                var controlParent = control.parent;

                if (!useRepositionMode)
                {
                    var posLink = target.GetPositionParentRB();
                    var hasPosLink = !ReferenceEquals(posLink, null);

                    var positionAfter = hasPosLink ? posLink.transform.InverseTransformPoint(snap.animatableRef.controller.transform.position) : control.localPosition;
                    var rotationAfter = hasPosLink ? Quaternion.Inverse(posLink.rotation) * snap.animatableRef.controller.transform.rotation : control.localRotation;

                    var positionBefore = snap.snapshot.position?.AsVector3() ?? positionAfter;
                    var rotationBefore = snap.snapshot.rotation?.AsQuaternion() ?? rotationAfter;

                    pivot = positionBefore;
                    positionDelta = positionAfter - positionBefore;
                    rotationDelta = Quaternion.Inverse(rotationBefore) * rotationAfter;
                }

                target.StartBulkUpdates();
                try
                {
                    var newKeyframes = new Dictionary<float, TransformStruct>();

                    // Faza 1: Oblicz nowe pozycje, ale ich nie stosuj
                    foreach (var key in target.GetAllKeyframesKeys())
                    {
                        var time = target.GetKeyframeTime(key);
                        if (time < from - 0.0001f || time > to + 0.001f) continue;

                        if (Math.Abs(time - offsetSnapshot.clipboard.time) < 0.0001f)
                        {
                            newKeyframes[time] = new TransformStruct
                            {
                                position = target.GetKeyframePosition(key),
                                rotation = target.GetKeyframeRotation(key)
                            };
                            continue;
                        }

                        var positionBefore = target.targetsPosition ? target.GetKeyframePosition(key) : control.position;
                        var rotationBefore = target.targetsRotation ? target.GetKeyframeRotation(key) : control.rotation;

                        Vector3 positionAfter;
                        Quaternion rotationAfter;

                        switch (offsetMode)
                        {
                            case ChangePivotMode:
                                positionAfter = rotationDelta * (positionBefore - pivot) + pivot + positionDelta;
                                rotationAfter = rotationBefore * rotationDelta;
                                break;
                            case OffsetMode:
                                positionAfter = positionBefore + positionDelta;
                                rotationAfter = rotationBefore * rotationDelta;
                                break;
                            case RepositionMode:
                                positionBefore = controlParent.TransformPoint(positionBefore);
                                positionAfter = rotationDelta * (positionBefore - pivot) + pivot + positionDelta;
                                rotationBefore = controlParent.TransformRotation(rotationBefore);
                                rotationAfter = rotationDelta * rotationBefore;
                                positionAfter = controlParent.InverseTransformPoint(positionAfter);
                                rotationAfter = controlParent.InverseTransformRotation(rotationAfter);
                                break;
                            default:
                                throw new NotImplementedException($"Offset mode '{offsetMode}' is not implemented");
                        }

                        newKeyframes[time] = new TransformStruct
                        {
                            position = positionAfter,
                            rotation = rotationAfter
                        };
                    }

                    if (smoothingDuration > 0.001f)
                    {
                        // Faza 2: Wygładzanie "przed" offsetem (Blend-in)
                        var blendInStart = Mathf.Max(0f, from - smoothingDuration);
                        var firstOffsetKeyframe = newKeyframes.OrderBy(kvp => kvp.Key).First().Value;

                        foreach (var key in target.GetAllKeyframesKeys())
                        {
                            var time = target.GetKeyframeTime(key);
                            if (time < blendInStart || time >= from) continue;

                            var alpha = (time - blendInStart) / smoothingDuration;
                            var originalPosition = target.GetKeyframePosition(key);
                            var originalRotation = target.GetKeyframeRotation(key);

                            var smoothedPosition = Vector3.Lerp(originalPosition, firstOffsetKeyframe.position, alpha);
                            var smoothedRotation = Quaternion.Slerp(originalRotation, firstOffsetKeyframe.rotation, alpha);

                            target.SetKeyframeByKey(key, smoothedPosition, smoothedRotation);
                        }

                        // Faza 3: Wygładzanie "po" offsecie (Blend-out)
                        var blendOutEnd = Mathf.Min(_clip.animationLength, to + smoothingDuration);
                        var lastOffsetKeyframe = newKeyframes.OrderByDescending(kvp => kvp.Key).First().Value;

                        foreach (var key in target.GetAllKeyframesKeys())
                        {
                            var time = target.GetKeyframeTime(key);
                            if (time <= to || time > blendOutEnd) continue;

                            var alpha = 1f - ((time - to) / smoothingDuration);
                            var originalPosition = target.GetKeyframePosition(key);
                            var originalRotation = target.GetKeyframeRotation(key);

                            var smoothedPosition = Vector3.Lerp(originalPosition, lastOffsetKeyframe.position, alpha);
                            var smoothedRotation = Quaternion.Slerp(originalRotation, lastOffsetKeyframe.rotation, alpha);

                            target.SetKeyframeByKey(key, smoothedPosition, smoothedRotation);
                        }
                    }

                    // Faza 4: Zastosuj właściwy offset
                    foreach (var kvp in newKeyframes)
                    {
                        var time = kvp.Key;
                        var newTransform = kvp.Value;
                        var key = target.GetKeyframeIndexByTime(time);
                        if (key != -1)
                        {
                            target.SetKeyframeByKey(key, newTransform.position, newTransform.rotation);
                        }
                    }
                }
                finally
                {
                    target.EndBulkUpdates();
                }
            }
        }

        public Snapshot Start(float clipTime, IEnumerable<FreeControllerV3AnimationTarget> targets, FreeControllerV3 mainController, string offsetMode)
        {
            if (offsetMode == RepositionMode)
            {
                if (ReferenceEquals(mainController, null)) throw new NullReferenceException($"{nameof(mainController)} cannot be null with {nameof(offsetMode)} {nameof(RepositionMode)}");
                mainController.canGrabPosition = true;
                mainController.canGrabRotation = true;
                mainController.currentPositionState = FreeControllerV3.PositionState.On;
                mainController.currentRotationState = FreeControllerV3.RotationState.On;
            }

            var clipboard = AtomAnimationClip.Copy(clipTime, targets.Cast<IAtomAnimationTarget>().ToList());
            if (clipboard.controllers.Count == 0)
            {
                SuperController.LogError($"Timeline: Cannot offset, no keyframes were found at time {clipTime}.");
                return null;
            }
            if (clipboard.controllers.Select(c => _clip.targetControllers.First(t => t.animatableRef == c.animatableRef)).Any(t => !t.EnsureParentAvailable(false)))
            {
                return null;
            }

            if (ReferenceEquals(mainController, null))
                return new Snapshot { clipboard = clipboard };

            return new Snapshot
            {
                mainController = mainController,
                previousMainPosition = mainController.control.position,
                previousMainRotation = mainController.control.rotation,
                clipboard = clipboard
            };
        }

        public class Snapshot
        {
            public FreeControllerV3 mainController;
            public Vector3 previousMainPosition;
            public Quaternion previousMainRotation;
            public AtomClipboardEntry clipboard;
        }
    }
}
