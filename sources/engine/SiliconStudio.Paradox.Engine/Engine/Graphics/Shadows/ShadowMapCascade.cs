﻿// <auto-generated>
// Do not edit this file yourself!
//
// This code was generated by Paradox Shader Mixin Code Generator.
// To generate it yourself, please install SiliconStudio.Paradox.VisualStudio.Package .vsix
// and re-save the associated .pdxfx.
// </auto-generated>

using System;
using SiliconStudio.Core;
using SiliconStudio.Paradox.Effects;
using SiliconStudio.Paradox.Graphics;
using SiliconStudio.Paradox.Shaders;
using SiliconStudio.Core.Mathematics;
using Buffer = SiliconStudio.Paradox.Graphics.Buffer;

namespace SiliconStudio.Paradox.Effects.Shadows
{
    internal static partial class ShadowMapCascadeKeys
    {
        public static readonly ParameterKey<float[]> CascadeDepthSplits = ParameterKeys.New<float[]>();
        public static readonly ParameterKey<Matrix[]> WorldToShadowCascadeUV = ParameterKeys.New<Matrix[]>();
    }
}
