﻿namespace Unreal.Core.Models
{
    /// <summary>
    /// https://github.com/EpicGames/UnrealEngine/blob/70bc980c6361d9a7d23f6d23ffe322a2d6ef16fb/Engine/Source/Runtime/Core/Public/Misc/NetworkGuid.h#L14
    /// </summary>
    public class NetworkGUID
    {
        public uint Value { get; set; }

        public bool IsValid()
        {
            return Value > 0;
        }

        public bool IsDynamic()
        {
            return Value > 0 && (Value & 1) != 1;
        }

        public bool IsDefault()
        {
            return Value == 1;
        }

        public bool IsStatic()
        {
            return (Value & 1) == 1;
        }
    }
}
