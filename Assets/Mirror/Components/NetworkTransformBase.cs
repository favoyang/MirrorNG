// vis2k:
// base class for NetworkTransform and NetworkTransformChild.
// New method is simple and stupid. No more 1500 lines of code.
//
// Server sends current data.
// Client saves it and interpolates last and latest data points.
//   Update handles transform movement / rotation
//   FixedUpdate handles rigidbody movement / rotation
//
// Notes:
// * Built-in Teleport detection in case of lags / teleport / obstacles
// * Quaternion > EulerAngles because gimbal lock and Quaternion.Slerp
// * Syncs XYZ. Works 3D and 2D. Saving 4 bytes isn't worth 1000 lines of code.
// * Initial delay might happen if server sends packet immediately after moving
//   just 1cm, hence we move 1cm and then wait 100ms for next packet
// * Only way for smooth movement is to use a fixed movement speed during
//   interpolation. interpolation over time is never that good.
//
using UnityEngine;

namespace Mirror
{
    public abstract class NetworkTransformBase : NetworkBehaviour
    {
        // rotation compression. not public so that other scripts can't modify
        // it at runtime. alternatively we could send 1 extra byte for the mode
        // each time so clients know how to decompress, but the whole point was
        // to save bandwidth in the first place.
        // -> can still be modified in the Inspector while the game is running,
        //    but would cause errors immediately and be pretty obvious.
        [Tooltip("Compresses 16 Byte Quaternion into None=12, Much=3, Lots=2 Byte")]
        [SerializeField] Compression compressRotation = Compression.Much;
        public enum Compression { None, Much, Lots, NoRotation }; // easily understandable and funny

        [Tooltip("Set to true if moves come from owner client, set to false if moves always come from server")]
        public bool clientAuthority;

        // is this a local player with authority over his own transform?
        bool isLocalPlayerWithAuthority => isLocalPlayer && clientAuthority;

        // server
        Vector3 lastPosition;
        Quaternion lastRotation;
        Vector3 lastScale;

        // client
        public class DataPoint
        {
            public float TimeStamp;
            // use local position/rotation for VR support
            public Vector3 LocalPosition;
            public Quaternion LocalRotation;
            public Vector3 LocalScale;
            public float MovementSpeed;
        }
        // interpolation start and goal
        DataPoint start;
        DataPoint goal;

        // local authority send time
        float lastClientSendTime;

        // target transform to sync. can be on a child.
        protected abstract Transform TargetComponent { get; }

        // serialization is needed by OnSerialize and by manual sending from authority
        static void SerializeIntoWriter(NetworkWriter writer, Vector3 position, Quaternion rotation, Compression compressRotation, Vector3 scale)
        {
            // serialize position
            writer.WriteVector3(position);

            // serialize rotation
            // writing quaternion = 16 byte
            // writing euler angles = 12 byte
            // -> quaternion->euler->quaternion always works.
            // -> gimbal lock only occurs when adding.
            Vector3 euler = rotation.eulerAngles;
            if (compressRotation == Compression.None)
            {
                // write 3 floats = 12 byte
                writer.WriteSingle(euler.x);
                writer.WriteSingle(euler.y);
                writer.WriteSingle(euler.z);
            }
            else if (compressRotation == Compression.Much)
            {
                // write 3 byte. scaling [0,360] to [0,255]
                writer.WriteByte(FloatBytePacker.ScaleFloatToByte(euler.x, 0, 360, byte.MinValue, byte.MaxValue));
                writer.WriteByte(FloatBytePacker.ScaleFloatToByte(euler.y, 0, 360, byte.MinValue, byte.MaxValue));
                writer.WriteByte(FloatBytePacker.ScaleFloatToByte(euler.z, 0, 360, byte.MinValue, byte.MaxValue));
            }
            else if (compressRotation == Compression.Lots)
            {
                // write 2 byte, 5 bits for each float
                writer.WriteUInt16(FloatBytePacker.PackThreeFloatsIntoUShort(euler.x, euler.y, euler.z, 0, 360));
            }

            // serialize scale
            writer.WriteVector3(scale);
        }

        public override bool OnSerialize(NetworkWriter writer, bool initialState)
        {
            // use local position/rotation/scale for VR support
            SerializeIntoWriter(writer, TargetComponent.transform.localPosition, TargetComponent.transform.localRotation, compressRotation, TargetComponent.transform.localScale);
            return true;
        }

        // try to estimate movement speed for a data point based on how far it
        // moved since the previous one
        // => if this is the first time ever then we use our best guess:
        //    -> delta based on transform.localPosition
        //    -> elapsed based on send interval hoping that it roughly matches
        static float EstimateMovementSpeed(DataPoint from, DataPoint to, Transform transform, float sendInterval)
        {
            Vector3 delta = to.LocalPosition - (from != null ? from.LocalPosition : transform.localPosition);
            float elapsed = from != null ? to.TimeStamp - from.TimeStamp : sendInterval;
            return elapsed > 0 ? delta.magnitude / elapsed : 0; // avoid NaN
        }

        // serialization is needed by OnSerialize and by manual sending from authority
        void DeserializeFromReader(NetworkReader reader)
        {
            // put it into a data point immediately
            var temp = new DataPoint
            {
                // deserialize position
                LocalPosition = reader.ReadVector3()
            };

            // deserialize rotation
            if (compressRotation == Compression.None)
            {
                // read 3 floats = 16 byte
                float x = reader.ReadSingle();
                float y = reader.ReadSingle();
                float z = reader.ReadSingle();
                temp.LocalRotation = Quaternion.Euler(x, y, z);
            }
            else if (compressRotation == Compression.Much)
            {
                // read 3 byte. scaling [0,255] to [0,360]
                float x = FloatBytePacker.ScaleByteToFloat(reader.ReadByte(), byte.MinValue, byte.MaxValue, 0, 360);
                float y = FloatBytePacker.ScaleByteToFloat(reader.ReadByte(), byte.MinValue, byte.MaxValue, 0, 360);
                float z = FloatBytePacker.ScaleByteToFloat(reader.ReadByte(), byte.MinValue, byte.MaxValue, 0, 360);
                temp.LocalRotation = Quaternion.Euler(x, y, z);
            }
            else if (compressRotation == Compression.Lots)
            {
                // read 2 byte, 5 bits per float
                Vector3 xyz = FloatBytePacker.UnpackUShortIntoThreeFloats(reader.ReadUInt16(), 0, 360);
                temp.LocalRotation = Quaternion.Euler(xyz.x, xyz.y, xyz.z);
            }

            temp.LocalScale = reader.ReadVector3();

            temp.TimeStamp = Time.time;

            // movement speed: based on how far it moved since last time
            // has to be calculated before 'start' is overwritten
            temp.MovementSpeed = EstimateMovementSpeed(goal, temp, TargetComponent.transform, syncInterval);

            // reassign start wisely
            // -> first ever data point? then make something up for previous one
            //    so that we can start interpolation without waiting for next.
            if (start == null)
            {
                start = new DataPoint
                {
                    TimeStamp = Time.time - syncInterval,
                    // local position/rotation for VR support
                    LocalPosition = TargetComponent.transform.localPosition,
                    LocalRotation = TargetComponent.transform.localRotation,
                    LocalScale = TargetComponent.transform.localScale,
                    MovementSpeed = temp.MovementSpeed
                };
            }
            // -> second or nth data point? then update previous, but:
            //    we start at where ever we are right now, so that it's
            //    perfectly smooth and we don't jump anywhere
            //
            //    example if we are at 'x':
            //
            //        A--x->B
            //
            //    and then receive a new point C:
            //
            //        A--x--B
            //              |
            //              |
            //              C
            //
            //    then we don't want to just jump to B and start interpolation:
            //
            //              x
            //              |
            //              |
            //              C
            //
            //    we stay at 'x' and interpolate from there to C:
            //
            //           x..B
            //            \ .
            //             \.
            //              C
            //
            else
            {
                float oldDistance = Vector3.Distance(start.LocalPosition, goal.LocalPosition);
                float newDistance = Vector3.Distance(goal.LocalPosition, temp.LocalPosition);

                start = goal;

                // teleport / lag / obstacle detection: only continue at current
                // position if we aren't too far away
                //
                // // local position/rotation for VR support
                if (Vector3.Distance(TargetComponent.transform.localPosition, start.LocalPosition) < oldDistance + newDistance)
                {
                    start.LocalPosition = TargetComponent.transform.localPosition;
                    start.LocalRotation = TargetComponent.transform.localRotation;
                    start.LocalScale = TargetComponent.transform.localScale;
                }
            }

            // set new destination in any case. new data is best data.
            goal = temp;
        }

        public override void OnDeserialize(NetworkReader reader, bool initialState)
        {
            // deserialize
            DeserializeFromReader(reader);
        }

        // local authority client sends sync message to server for broadcasting
        [Command]
        void CmdClientToServerSync(byte[] payload)
        {
            // deserialize payload
            var reader = new NetworkReader(payload);
            DeserializeFromReader(reader);

            // server-only mode does no interpolation to save computations,
            // but let's set the position directly
            if (isServer && !isClient)
                ApplyPositionRotationScale(goal.LocalPosition, goal.LocalRotation, goal.LocalScale);

            // set dirty so that OnSerialize broadcasts it
            SetDirtyBit(1UL);
        }

        // where are we in the timeline between start and goal? [0,1]
        static float CurrentInterpolationFactor(DataPoint start, DataPoint goal)
        {
            if (start != null)
            {
                float difference = goal.TimeStamp - start.TimeStamp;

                // the moment we get 'goal', 'start' is supposed to
                // start, so elapsed time is based on:
                float elapsed = Time.time - goal.TimeStamp;
                return difference > 0 ? elapsed / difference : 0; // avoid NaN
            }
            return 0;
        }

        static Vector3 InterpolatePosition(DataPoint start, DataPoint goal, Vector3 currentPosition)
        {
            if (start != null)
            {
                // Option 1: simply interpolate based on time. but stutter
                // will happen, it's not that smooth. especially noticeable if
                // the camera automatically follows the player
                //   float t = CurrentInterpolationFactor();
                //   return Vector3.Lerp(start.position, goal.position, t);

                // Option 2: always += speed
                // -> speed is 0 if we just started after idle, so always use max
                //    for best results
                float speed = Mathf.Max(start.MovementSpeed, goal.MovementSpeed);
                return Vector3.MoveTowards(currentPosition, goal.LocalPosition, speed * Time.deltaTime);
            }
            return currentPosition;
        }

        static Quaternion InterpolateRotation(DataPoint start, DataPoint goal, Quaternion defaultRotation)
        {
            if (start != null)
            {
                float t = CurrentInterpolationFactor(start, goal);
                return Quaternion.Slerp(start.LocalRotation, goal.LocalRotation, t);
            }
            return defaultRotation;
        }

        static Vector3 InterpolateScale(DataPoint start, DataPoint goal, Vector3 currentScale)
        {
            if (start != null)
            {
                float t = CurrentInterpolationFactor(start, goal);
                return Vector3.Lerp(start.LocalScale, goal.LocalScale, t);
            }
            return currentScale;
        }

        // teleport / lag / stuck detection
        // -> checking distance is not enough since there could be just a tiny
        //    fence between us and the goal
        // -> checking time always works, this way we just teleport if we still
        //    didn't reach the goal after too much time has elapsed
        bool NeedsTeleport()
        {
            // calculate time between the two data points
            float startTime = start != null ? start.TimeStamp : Time.time - syncInterval;
            float goalTime = goal != null ? goal.TimeStamp : Time.time;
            float difference = goalTime - startTime;
            float timeSinceGoalReceived = Time.time - goalTime;
            return timeSinceGoalReceived > difference * 5;
        }

        // moved since last time we checked it?
        bool HasEitherMovedRotatedScaled()
        {
            // moved or rotated or scaled?
            // local position/rotation/scale for VR support
            bool moved = lastPosition != TargetComponent.transform.localPosition;
            bool rotated = lastRotation != TargetComponent.transform.localRotation;
            bool scaled = lastScale != TargetComponent.transform.localScale;

            // save last for next frame to compare
            // (only if change was detected. otherwise slow moving objects might
            //  never sync because of C#'s float comparison tolerance. see also:
            //  https://github.com/vis2k/Mirror/pull/428)
            bool change = moved || rotated || scaled;
            if (change)
            {
                // local position/rotation for VR support
                lastPosition = TargetComponent.transform.localPosition;
                lastRotation = TargetComponent.transform.localRotation;
                lastScale = TargetComponent.transform.localScale;
            }
            return change;
        }

        // set position carefully depending on the target component
        void ApplyPositionRotationScale(Vector3 position, Quaternion rotation, Vector3 scale)
        {
            // local position/rotation for VR support
            TargetComponent.transform.localPosition = position;
            if (Compression.NoRotation != compressRotation)
            {
                TargetComponent.transform.localRotation = rotation;
            }
            TargetComponent.transform.localScale = scale;
        }

        void Update()
        {
            // if server then always sync to others.
            if (isServer)
            {
                // just use OnSerialize via SetDirtyBit only sync when position
                // changed. set dirty bits 0 or 1
                SetDirtyBit(HasEitherMovedRotatedScaled() ? 1UL : 0UL);
            }

            // no 'else if' since host mode would be both
            if (isClient)
            {
                // send to server if we have local authority (and aren't the server)
                // -> only if connectionToServer has been initialized yet too
                if (!isServer && isLocalPlayerWithAuthority)
                {
                    // check only each 'syncInterval'
                    if (Time.time - lastClientSendTime >= syncInterval)
                    {
                        if (HasEitherMovedRotatedScaled())
                        {
                            // serialize
                            // local position/rotation for VR support
                            var writer = new NetworkWriter();
                            SerializeIntoWriter(writer, TargetComponent.transform.localPosition, TargetComponent.transform.localRotation, compressRotation, TargetComponent.transform.localScale);

                            // send to server
                            CmdClientToServerSync(writer.ToArray());
                        }
                        lastClientSendTime = Time.time;
                    }
                }

                // apply interpolation on client for all players
                // unless this client has authority over the object. could be
                // himself or another object that he was assigned authority over
                if (!isLocalPlayerWithAuthority)
                {
                    // received one yet? (initialized?)
                    if (goal != null)
                    {
                        // teleport or interpolate
                        if (NeedsTeleport())
                        {
                            // local position/rotation for VR support
                            ApplyPositionRotationScale(goal.LocalPosition, goal.LocalRotation, goal.LocalScale);
                        }
                        else
                        {
                            // local position/rotation for VR support
                            ApplyPositionRotationScale(InterpolatePosition(start, goal, TargetComponent.transform.localPosition),
                                                       InterpolateRotation(start, goal, TargetComponent.transform.localRotation),
                                                       InterpolateScale(start, goal, TargetComponent.transform.localScale));
                        }
                    }
                }
            }
        }

        static void DrawDataPointGizmo(DataPoint data, Color color)
        {
            // use a little offset because transform.localPosition might be in
            // the ground in many cases
            Vector3 offset = Vector3.up * 0.01f;

            // draw position
            Gizmos.color = color;
            Gizmos.DrawSphere(data.LocalPosition + offset, 0.5f);

            // draw forward and up
            Gizmos.color = Color.blue; // like unity move tool
            Gizmos.DrawRay(data.LocalPosition + offset, data.LocalRotation * Vector3.forward);

            Gizmos.color = Color.green; // like unity move tool
            Gizmos.DrawRay(data.LocalPosition + offset, data.LocalRotation * Vector3.up);
        }

        static void DrawLineBetweenDataPoints(DataPoint data1, DataPoint data2, Color color)
        {
            Gizmos.color = color;
            Gizmos.DrawLine(data1.LocalPosition, data2.LocalPosition);
        }

        // draw the data points for easier debugging
        void OnDrawGizmos()
        {
            // draw start and goal points
            if (start != null) DrawDataPointGizmo(start, Color.gray);
            if (goal != null) DrawDataPointGizmo(goal, Color.white);

            // draw line between them
            if (start != null && goal != null) DrawLineBetweenDataPoints(start, goal, Color.cyan);
        }
    }
}
