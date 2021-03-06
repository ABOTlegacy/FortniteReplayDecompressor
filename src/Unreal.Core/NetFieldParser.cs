﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using FastMember;
using Unreal.Core.Attributes;
using Unreal.Core.Contracts;
using Unreal.Core.Models;
using Unreal.Core.Models.Enums;

namespace Unreal.Core
{
    public class NetFieldParser
    {
        public static bool IncludeOnlyMode { get; set; } = true;
        public static HashSet<Type> IncludedExportGroups { get; private set; } = new HashSet<Type>();

        private static Dictionary<string, Type> _netFieldGroups = new Dictionary<string, Type>();
        private static Dictionary<Type, NetFieldGroupInfo> _netFieldGroupInfo = new Dictionary<Type, NetFieldGroupInfo>();
        private static Dictionary<Type, RepLayoutCmdType> _primitiveTypeLayout = new Dictionary<Type, RepLayoutCmdType>();
        public static Dictionary<string, HashSet<UnknownFieldInfo>> UnknownNetFields { get; private set; } = new Dictionary<string, HashSet<UnknownFieldInfo>>();

        public static bool HasNewNetFields => UnknownNetFields.Count > 0;

        static NetFieldParser()
        {
            IEnumerable<Type> netFields = Assembly.GetExecutingAssembly().GetTypes().Where(x => x.GetCustomAttribute<NetFieldExportGroupAttribute>() != null);

            foreach (Type type in netFields)
            {
                NetFieldExportGroupAttribute attribute = type.GetCustomAttribute<NetFieldExportGroupAttribute>();

                if (attribute != null)
                {
                    _netFieldGroups[attribute.Path] = type;
                }

                NetFieldGroupInfo info = new NetFieldGroupInfo();

                _netFieldGroupInfo[type] = info;

                foreach (PropertyInfo property in type.GetProperties())
                {
                    NetFieldExportAttribute netFieldExportAttribute = property.GetCustomAttribute<NetFieldExportAttribute>();

                    if(netFieldExportAttribute == null)
                    {
                        continue;
                    }

                    info.Properties[netFieldExportAttribute.Name] = new NetFieldInfo
                    {
                        Attribute = netFieldExportAttribute,
                        PropertyInfo = property
                    };
                }
            }

            //Type layout for dynamic arrays
            _primitiveTypeLayout.Add(typeof(bool), RepLayoutCmdType.PropertyBool);
            _primitiveTypeLayout.Add(typeof(byte), RepLayoutCmdType.PropertyByte);
            _primitiveTypeLayout.Add(typeof(ushort), RepLayoutCmdType.PropertyUInt16);
            _primitiveTypeLayout.Add(typeof(int), RepLayoutCmdType.PropertyInt);
            _primitiveTypeLayout.Add(typeof(uint), RepLayoutCmdType.PropertyUInt32);
            _primitiveTypeLayout.Add(typeof(ulong), RepLayoutCmdType.PropertyUInt64);
            _primitiveTypeLayout.Add(typeof(float), RepLayoutCmdType.PropertyFloat);
            _primitiveTypeLayout.Add(typeof(string), RepLayoutCmdType.PropertyString);

            IEnumerable<Type> iPropertyTypes = AppDomain.CurrentDomain.GetAssemblies().SelectMany(x => x.GetTypes())
                .Where(x => typeof(IProperty).IsAssignableFrom(x) && !x.IsInterface && !x.IsAbstract);

            //Allows deserializing IProperty type arrays
            foreach (var iPropertyType in iPropertyTypes)
            {
                _primitiveTypeLayout.Add(iPropertyType, RepLayoutCmdType.Property);
            }

            _primitiveTypeLayout.Add(typeof(object), RepLayoutCmdType.Ignore);

            AddDefaultExportGroups();
        }
        
        private static void AddDefaultExportGroups()
        {
            //Player info
            IncludedExportGroups.Add(typeof(FortPlayerState));
            IncludedExportGroups.Add(typeof(PlayerPawnC));
            IncludedExportGroups.Add(typeof(FortInventory));
            IncludedExportGroups.Add(typeof(FortPickup));

            //Game state
            IncludedExportGroups.Add(typeof(GameStateC));
            IncludedExportGroups.Add(typeof(SafeZoneIndicatorC));
            IncludedExportGroups.Add(typeof(AircraftC));

            //Supply drops / llamas
            IncludedExportGroups.Add(typeof(SupplyDropC));
            IncludedExportGroups.Add(typeof(SupplyDropLlamaC));
            IncludedExportGroups.Add(typeof(SupplyDropBalloonC));

            //////Projectiles
            //IncludedExportGroups.Add(typeof(BPrjBulletSniperC));
            //IncludedExportGroups.Add(typeof(BPrjBulletSniperHeavyC));
            //IncludedExportGroups.Add(typeof(BPrjLotusMustacheC));
            //IncludedExportGroups.Add(typeof(BPrjArrowExplodeOnImpactC));
            //IncludedExportGroups.Add(typeof(BPrjBulletSniperAutoChildC));

            //All weapons
            /*foreach(KeyValuePair<Type, NetFieldGroupInfo> type in _netFieldGroupInfo.Where(x => x.Value.Properties.Any(y => y.Key == "WeaponData")))
            {
                IncludedExportGroups.Add(type.Key);
            }*/
        }

        public static bool WillReadType(string group)
        {
            if(_netFieldGroups.ContainsKey(group))
            {
                Type type = _netFieldGroups[group];

                if(IncludedExportGroups.Contains(type))
                {
                    return true;
                }

                return false;
            }

            return false;
        }

        public static void ReadField(object obj, NetFieldExport export, NetFieldExportGroup exportGroup, uint handle, NetBitReader netBitReader)
        {
            string group = exportGroup.PathName;

            string fixedExportName = FixInvalidNames(export.Name);

            if(!_netFieldGroups.ContainsKey(group))
            {
                AddUnknownField(fixedExportName, export?.Type, group, handle, netBitReader);

                return;
            }

            Type netType = _netFieldGroups[group];
            NetFieldGroupInfo netGroupInfo = _netFieldGroupInfo[netType];

            if(!netGroupInfo.Properties.ContainsKey(fixedExportName))
            {
                AddUnknownField(fixedExportName, export?.Type, group, handle, netBitReader);

                return;
            }

            NetFieldInfo netFieldInfo = netGroupInfo.Properties[fixedExportName];

            //Update if it finds a higher bit count or an actual type
            if(!String.IsNullOrEmpty(export.Type))
            {
                if(String.IsNullOrEmpty(netFieldInfo.Attribute.Info.Type))
                {
                    AddUnknownField(fixedExportName, export?.Type, group, handle, netBitReader);
                }
            }
            /*else if(netFieldInfo.Attribute.Info.BitCount < netBitReader.GetBitsLeft())
            {
                if(String.IsNullOrEmpty(netFieldInfo.Attribute.Info.Type))
                {
                    AddUnknownField(fixedExportName, export?.Type, group, handle, netBitReader);
                }
            }*/

            SetType(obj, netType, netFieldInfo, exportGroup, netBitReader);
        }

        private static object ReadDataType(RepLayoutCmdType replayout, NetBitReader netBitReader, Type objectType = null)
        {
            object data = null;

            switch(replayout)
            {
                case RepLayoutCmdType.Property:
                    data = Activator.CreateInstance(objectType);
                    (data as IProperty).Serialize(netBitReader);
                    break;
                case RepLayoutCmdType.PropertyBool:
                    data = netBitReader.SerializePropertyBool();
                    break;
                case RepLayoutCmdType.PropertyName:
                    netBitReader.Seek(netBitReader.Position + netBitReader.GetBitsLeft());
                    break;
                case RepLayoutCmdType.PropertyFloat:
                    data = netBitReader.SerializePropertyFloat();
                    break;
                case RepLayoutCmdType.PropertyNativeBool:
                    data = netBitReader.SerializePropertyNativeBool();
                    break;
                case RepLayoutCmdType.PropertyNetId:
                    data = netBitReader.SerializePropertyNetId();
                    break;
                case RepLayoutCmdType.PropertyObject:
                    data = netBitReader.SerializePropertyObject();
                    break;
                case RepLayoutCmdType.PropertyPlane:
                    throw new NotImplementedException("Plane RepLayoutCmdType not implemented");
                case RepLayoutCmdType.PropertyRotator:
                    data = netBitReader.SerializePropertyRotator();
                    break;
                case RepLayoutCmdType.PropertyString:
                    data = netBitReader.SerializePropertyString();
                    break;
                case RepLayoutCmdType.PropertyVector10:
                    data = netBitReader.SerializePropertyVector10();
                    break;
                case RepLayoutCmdType.PropertyVector100:
                    data = netBitReader.SerializePropertyVector100();
                    break;
                case RepLayoutCmdType.PropertyVectorNormal:
                    data = netBitReader.SerializePropertyVectorNormal();
                    break;
                case RepLayoutCmdType.PropertyVectorQ:
                    data = netBitReader.SerializePropertyQuantizeVector();
                    break;
                case RepLayoutCmdType.RepMovement:
                    data = netBitReader.SerializeRepMovement();
                    break;
                case RepLayoutCmdType.Enum:
                    data = netBitReader.SerializeEnum();
                    break;
                //Auto generation fix to handle 1-8 bits
                case RepLayoutCmdType.PropertyByte:
                    data = (byte)netBitReader.ReadBitsToInt(netBitReader.GetBitsLeft());
                    break;
                //Auto generation fix to handle 1-32 bits. 
                case RepLayoutCmdType.PropertyInt:
                    data = netBitReader.ReadBitsToInt(netBitReader.GetBitsLeft());
                    break;
                case RepLayoutCmdType.PropertyUInt64:
                    data = netBitReader.ReadUInt64();
                    break;
                case RepLayoutCmdType.PropertyUInt16:
                    data = (ushort)netBitReader.ReadBitsToInt(netBitReader.GetBitsLeft());
                    break;
                case RepLayoutCmdType.PropertyUInt32:
                    data = netBitReader.ReadUInt32();
                    break;
                case RepLayoutCmdType.Pointer:
                    switch (netBitReader.GetBitsLeft())
                    {
                        case 8:
                            data = (uint)netBitReader.ReadByte();
                            break;
                        case 16:
                            data = (uint)netBitReader.ReadUInt16();
                            break;
                        case 32:
                            data = netBitReader.ReadUInt32();
                            break;
                    }
                    break;
                case RepLayoutCmdType.PropertyVector:
                    data = new FVector(netBitReader.ReadSingle(), netBitReader.ReadSingle(), netBitReader.ReadSingle());
                    break;
                case RepLayoutCmdType.Ignore:
                    netBitReader.Seek(netBitReader.Position + netBitReader.GetBitsLeft());
                    break;
            }

            return data;
        }

        private static void SetType(object obj, Type netType, NetFieldInfo netFieldInfo, NetFieldExportGroup exportGroup, NetBitReader netBitReader)
        {
            object data = null;

            switch (netFieldInfo.Attribute.Type)
            {
                case RepLayoutCmdType.DynamicArray:
                    data = ReadArrayField(obj, exportGroup, netFieldInfo, netBitReader);
                    break;
                default:
                    data = ReadDataType(netFieldInfo.Attribute.Type, netBitReader, netFieldInfo.PropertyInfo.PropertyType);
                    break;
            }

            if (data != null)
            {
                TypeAccessor typeAccessor = TypeAccessor.Create(netType);
                typeAccessor[obj, netFieldInfo.PropertyInfo.Name] = data;
            }
        }

        private static Array ReadArrayField(object obj, NetFieldExportGroup netfieldExportGroup, NetFieldInfo fieldInfo, NetBitReader netBitReader)
        {
            uint arrayIndexes = netBitReader.ReadIntPacked();

            Type elementType = fieldInfo.PropertyInfo.PropertyType.GetElementType();
            RepLayoutCmdType replayout = RepLayoutCmdType.Ignore;

            NetFieldGroupInfo groupInfo = null;

            if(_netFieldGroupInfo.ContainsKey(elementType))
            {
                groupInfo = _netFieldGroupInfo[elementType];
            }
            else
            {
                if (!_primitiveTypeLayout.TryGetValue(elementType, out replayout))
                {
                    replayout = RepLayoutCmdType.Ignore;
                }
                else
                {
                    if (elementType == typeof(DebuggingObject))
                    {

                    }
                }
            }


            Array arr = Array.CreateInstance(elementType, arrayIndexes);

            while(true)
            {
                uint index = netBitReader.ReadIntPacked();

                if(index == 0)
                {
                    if(netBitReader.GetBitsLeft() == 8)
                    {
                        uint terminator = netBitReader.ReadIntPacked();

                        if(terminator != 0x00)
                        {
                            //Log error

                            return arr; 
                        }
                    }

                    return arr;
                }

                --index;

                if(index >= arrayIndexes)
                {
                    //Log error

                    return arr;
                }

                object data = null;

                if (groupInfo != null)
                {
                    data = Activator.CreateInstance(elementType);
                }

                while (true)
                {
                    uint handle = netBitReader.ReadIntPacked(); 
                    
                    if (handle == 0)
                    {
                        break;
                    }

                    handle--;

                    if (netfieldExportGroup.NetFieldExports.Length < handle)
                    {
                        return arr;
                    }

                    NetFieldExport export = netfieldExportGroup.NetFieldExports[handle];
                    uint numBits = netBitReader.ReadIntPacked();

                    if (numBits == 0)
                    {
                        continue;
                    }

                    if (export == null)
                    {
                        netBitReader.SkipBits((int)numBits);

                        continue;
                    }

                    NetBitReader cmdReader = new NetBitReader(netBitReader.ReadBits(numBits))
                    {
                        EngineNetworkVersion = netBitReader.EngineNetworkVersion,
                        NetworkVersion = netBitReader.NetworkVersion
                    };

                    //Uses the same type for the array
                    if (groupInfo != null)
                    {
                        ReadField(data, export, netfieldExportGroup, handle, cmdReader);
                    }
                    else //Probably primitive values
                    {
                        data = ReadDataType(replayout, cmdReader, elementType);
                    }
                }

                arr.SetValue(data, index);
            }
        }

        public static INetFieldExportGroup CreateType(string group)
        {
            if(!_netFieldGroups.ContainsKey(group))
            {
                return null;
            }

            return (INetFieldExportGroup)Activator.CreateInstance(_netFieldGroups[group]);
        }

        public static void GenerateFiles(string directory)
        {
            if(Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }

            Directory.CreateDirectory(directory);

            foreach(KeyValuePair<string, Type> netFieldGroundKvp in _netFieldGroups)
            {
                if(!_netFieldGroupInfo.TryGetValue(netFieldGroundKvp.Value, out NetFieldGroupInfo groupInfo))
                {
                    continue;
                }

                foreach (KeyValuePair<string, NetFieldInfo> netFieldInfo in groupInfo.Properties)
                {
                    AddUnknownField(netFieldGroundKvp.Key, netFieldInfo.Value.Attribute.Info);
                }
            }


            foreach (KeyValuePair<string, HashSet<UnknownFieldInfo>> kvp in UnknownNetFields)
            {
                string fileName = String.Join("", kvp.Key.Split('/').Last().Split('.').Last().Split("_")).Replace("Athena", "");
                fileName = FixInvalidNames(fileName);

                if (char.IsDigit(fileName[0]))
                {
                    int firstCharacter = fileName.ToList().FindIndex(x => !char.IsDigit(x));

                    fileName = fileName.Substring(firstCharacter);
                }


                StringBuilder builder = new StringBuilder();
                builder.AppendLine("using System.Collections.Generic;");
                builder.AppendLine("using Unreal.Core.Attributes;");
                builder.AppendLine("using Unreal.Core.Contracts;");
                builder.AppendLine("using Unreal.Core.Models.Enums;\n");
                builder.AppendLine("namespace Unreal.Core.Models");
                builder.AppendLine("{");
                builder.AppendLine($"\t[NetFieldExportGroup(\"{kvp.Key}\")]");
                builder.AppendLine($"\tpublic class {fileName} : INetFieldExportGroup");
                builder.AppendLine("\t{");

                foreach (UnknownFieldInfo unknownField in kvp.Value.OrderBy(x => x.Handle))
                {
                    RepLayoutCmdType commandType = RepLayoutCmdType.Ignore;
                    string type = "object";

                    if(!String.IsNullOrEmpty(unknownField.Type))
                    {
                        //8, 16, or 32
                        if(unknownField.Type.EndsWith("*") || unknownField.Type.StartsWith("TSubclassOf"))
                        {
                            type = "uint?";
                            commandType = RepLayoutCmdType.Pointer;
                        }
                        else if (unknownField.Type.StartsWith("TEnumAsByte"))
                        {
                            type = "int?";
                            commandType = RepLayoutCmdType.Enum;
                        }
                        else if (unknownField.Type.StartsWith("E") && unknownField.Type.Length > 1 && Char.IsUpper(unknownField.Type[1]))
                        {
                            type = "int?";
                            commandType = RepLayoutCmdType.Enum;
                        }
                        else
                        {
                            switch (unknownField.Type)
                            {
                                case "TArray":
                                    type = "object[]";
                                    commandType = RepLayoutCmdType.DynamicArray;
                                    break;
                                case "FRotator":
                                    type = "FRotator";
                                    commandType = RepLayoutCmdType.PropertyRotator;
                                    break;
                                case "float":
                                    type = "float?";
                                    commandType = RepLayoutCmdType.PropertyFloat;
                                    break;
                                case "bool":
                                    type = "bool?";
                                    commandType = RepLayoutCmdType.PropertyBool;
                                    break;
                                case "int8":
                                    if (unknownField.BitCount == 1)
                                    {
                                        type = "bool?";
                                        commandType = RepLayoutCmdType.PropertyBool;
                                    }
                                    else
                                    {
                                        type = "byte?";
                                        commandType = RepLayoutCmdType.PropertyByte;
                                    }
                                    break;
                                case "uint8":
                                    if (unknownField.BitCount == 1)
                                    {
                                        type = "bool?";
                                        commandType = RepLayoutCmdType.PropertyBool;
                                    }
                                    else
                                    {
                                        type = "byte?";
                                        commandType = RepLayoutCmdType.PropertyByte;
                                    }
                                    break;
                                case "int16":
                                    type = "ushort?";
                                    commandType = RepLayoutCmdType.PropertyUInt16;
                                    break;
                                case "uint16":
                                    type = "ushort?";
                                    commandType = RepLayoutCmdType.PropertyUInt16;
                                    break;
                                case "uint32":
                                    type = "uint?";
                                    commandType = RepLayoutCmdType.PropertyUInt32;
                                    break;
                                case "int32":
                                    type = "int?";
                                    commandType = RepLayoutCmdType.PropertyInt;
                                    break;
                                case "FUniqueNetIdRepl":
                                    type = "string";
                                    commandType = RepLayoutCmdType.PropertyNetId;
                                    break;
                                case "FHitResult":
                                case "FGameplayTag":
                                case "FText":
                                case "FVector2D":
                                case "FAthenaPawnReplayData":
                                case "FDateTime":
                                case "FName":
                                case "FQuat":
                                case "FVector":
                                case "FQuantizedBuildingAttribute":
                                    type = unknownField.Type;
                                    commandType = RepLayoutCmdType.Property;
                                    break;
                                case "FVector_NetQuantize":
                                    type = "FVector";
                                    commandType = RepLayoutCmdType.PropertyVectorQ;
                                    break;
                                case "FVector_NetQuantize10":
                                    type = "FVector";
                                    commandType = RepLayoutCmdType.PropertyVector10;
                                    break;
                                case "FVector_NetQuantizeNormal":
                                    type = "FVector";
                                    commandType = RepLayoutCmdType.PropertyVectorNormal;
                                    break;
                                case "FVector_NetQuantize100":
                                    type = "FVector";
                                    commandType = RepLayoutCmdType.PropertyVector100;
                                    break;
                                case "FString":
                                    type = "string";
                                    commandType = RepLayoutCmdType.PropertyString;
                                    break;
                                case "FRepMovement":
                                    type = "FRepMovement";
                                    commandType = RepLayoutCmdType.RepMovement;
                                    break;
                                case "FMinimalGameplayCueReplicationProxy":
                                    type = "int?";
                                    commandType = RepLayoutCmdType.Enum;
                                    break;
                                default:
                                    //Console.WriteLine(unknownField.Type);
                                    break;
                            }
                        }
                    }
                    else
                    {
                        switch (unknownField.BitCount)
                        {
                            case 1:
                                type = "bool?";
                                commandType = RepLayoutCmdType.PropertyBool;
                                break;
                            case 8: //Can't determine if it's a pointer that can have 8, 16, or 32 bits
                            case 16:
                            case 32:
                                type = "uint?";
                                commandType = RepLayoutCmdType.PropertyUInt32;
                                break;
                        }
                    }

                    string fixedPropertyName = FixInvalidNames(unknownField.PropertyName);

                    builder.AppendLine($"\t\t[NetFieldExport(\"{unknownField.PropertyName}\", RepLayoutCmdType.{commandType.ToString()}, {unknownField.Handle}, \"{unknownField.PropertyName}\", \"{unknownField.Type}\", {unknownField.BitCount})]");
                    builder.AppendLine($"\t\tpublic {type} {fixedPropertyName} {{ get; set; }} //Type: {unknownField.Type} Bits: {unknownField.BitCount}\n");
                }

                builder.AppendLine("\t}");
                builder.AppendLine("}");

                string cSharpFile = builder.ToString();

                File.WriteAllText(Path.Combine(directory, fileName) + ".cs", cSharpFile);
            }
        }

        private static unsafe string FixInvalidNames(string str)
        {
            int len = str.Length;
            char* newChars = stackalloc char[len];
            char* currentChar = newChars;

            for (int i = 0; i < len; ++i)
            {
                char c = str[i];

                bool isDigit = (c ^ '0') <= 9;

                byte val = (byte)((c & 0xDF) - 0x40);
                bool isChar = val > 0 && val <= 26;

                if(isDigit || isChar)
                {
                    *currentChar++ = c;
                }
            }

            return new string(newChars, 0, (int)(currentChar - newChars));
        }

        private static void AddUnknownField(string group, UnknownFieldInfo fieldInfo)
        {
            HashSet<UnknownFieldInfo> fields = new HashSet<UnknownFieldInfo>();

            if (!UnknownNetFields.TryAdd(group, fields))
            {
                UnknownNetFields.TryGetValue(group, out fields);
            }

            fields.Add(fieldInfo);

        }

        private static void AddUnknownField(string exportName, string exportType, string group, uint handle, NetBitReader netBitReader)
        {
            HashSet<UnknownFieldInfo> fields = new HashSet<UnknownFieldInfo>();

            if (!UnknownNetFields.TryAdd(group, fields))
            {
                UnknownNetFields.TryGetValue(group, out fields);
            }

            fields.Add(new UnknownFieldInfo(FixInvalidNames(exportName), exportType, netBitReader.GetBitsLeft(), handle));

        }

        private class NetFieldGroupInfo
        {
            public Dictionary<string, NetFieldInfo> Properties { get; set; } = new Dictionary<string, NetFieldInfo>();
        }

        private class NetFieldInfo
        {
            public NetFieldExportAttribute Attribute { get; set; }
            public PropertyInfo PropertyInfo { get; set; }
        }
    }

    public class UnknownFieldInfo
    {
        public string PropertyName { get; set; }
        public string Type { get; set; }
        public int BitCount { get; set; }
        public uint Handle { get; set; }

        public UnknownFieldInfo(string propertyname, string type, int bitCount, uint handle)
        {
            PropertyName = propertyname;
            Type = type;
            BitCount = bitCount;
            Handle = handle;
        }

        public override bool Equals(object obj)
        {
            UnknownFieldInfo fieldInfo = obj as UnknownFieldInfo;

            if (fieldInfo == null)
            {
                return base.Equals(obj);
            }

            return fieldInfo.PropertyName == PropertyName;
        }

        public override int GetHashCode()
        {
            return PropertyName.GetHashCode();
        }
    }
}
