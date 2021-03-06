// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;

using Internal.Reflection.Augments;

namespace System.Reflection
{
    public abstract class FieldInfo : MemberInfo
    {
        protected FieldInfo() { }

        public override MemberTypes MemberType => MemberTypes.Field;

        public abstract FieldAttributes Attributes { get; }
        public abstract Type FieldType { get; }

        public bool IsInitOnly => (Attributes & FieldAttributes.InitOnly) != 0;
        public bool IsLiteral => (Attributes & FieldAttributes.Literal) != 0;
        public bool IsNotSerialized => (Attributes & FieldAttributes.NotSerialized) != 0;
        public bool IsPinvokeImpl => (Attributes & FieldAttributes.PinvokeImpl) != 0;
        public bool IsSpecialName => (Attributes & FieldAttributes.SpecialName) != 0;
        public bool IsStatic => (Attributes & FieldAttributes.Static) != 0;

        public bool IsAssembly => (Attributes & FieldAttributes.FieldAccessMask) == FieldAttributes.Assembly;
        public bool IsFamily => (Attributes & FieldAttributes.FieldAccessMask) == FieldAttributes.Family;
        public bool IsFamilyAndAssembly => (Attributes & FieldAttributes.FieldAccessMask) == FieldAttributes.FamANDAssem;
        public bool IsFamilyOrAssembly => (Attributes & FieldAttributes.FieldAccessMask) == FieldAttributes.FamORAssem;
        public bool IsPrivate => (Attributes & FieldAttributes.FieldAccessMask) == FieldAttributes.Private;
        public bool IsPublic => (Attributes & FieldAttributes.FieldAccessMask) == FieldAttributes.Public;

        public virtual bool IsSecurityCritical { get { throw new NotImplementedException(); } }
        public virtual bool IsSecuritySafeCritical { get { throw new NotImplementedException(); } }
        public virtual bool IsSecurityTransparent { get { throw new NotImplementedException(); } }

        public abstract RuntimeFieldHandle FieldHandle { get; }
        public static FieldInfo GetFieldFromHandle(RuntimeFieldHandle handle) => ReflectionAugments.ReflectionCoreCallbacks.GetFieldFromHandle(handle);
        public static FieldInfo GetFieldFromHandle(RuntimeFieldHandle handle, RuntimeTypeHandle declaringType) => ReflectionAugments.ReflectionCoreCallbacks.GetFieldFromHandle(handle, declaringType);

        public override bool Equals(object obj) => base.Equals(obj);
        public override int GetHashCode() => base.GetHashCode();

        public static bool operator ==(FieldInfo left, FieldInfo right)
        {
            if (object.ReferenceEquals(left, right))
                return true;

            if ((object)left == null || (object)right == null)
                return false;

            return left.Equals(right);
        }

        public static bool operator !=(FieldInfo left, FieldInfo right) => !(left == right);

        public abstract object GetValue(object obj);

        public void SetValue(object obj, object value) => SetValue(obj, value, BindingFlags.Default, Type._GetDefaultBinder(), null);
        public abstract void SetValue(object obj, object value, BindingFlags invokeAttr, Binder binder, CultureInfo culture);

        public virtual object GetRawConstantValue() { throw new NotSupportedException(SR.NotSupported_AbstractNonCLS); }

        public virtual Type[] GetOptionalCustomModifiers() { throw NotImplemented.ByDesign; }
        public virtual Type[] GetRequiredCustomModifiers() { throw NotImplemented.ByDesign; }
    }
}
