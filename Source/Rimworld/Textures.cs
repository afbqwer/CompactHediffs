using UnityEngine;
using Verse;

namespace PeteTimesSix.CompactHediffs.Rimworld
{
    [StaticConstructorOnStartup]
    public static class Textures
    {
        public static readonly Texture2D translucentWhite = SolidColorMaterials.NewSolidColorTexture(new Color(1f, 1f, 1f, 0.5f));

        public static readonly Texture2D InfoButtonNarrow = ContentFinder<Texture2D>.Get("infoNarrow");

        public static readonly Texture2D Vanilla_BleedingIcon = ContentFinder<Texture2D>.Get("UI/Icons/Medical/Bleeding");
        public static readonly Texture2D Vanilla_TendedIcon_Well_General = ContentFinder<Texture2D>.Get("UI/Icons/Medical/TendedWell");
        public static readonly Texture2D Vanilla_TendedIcon_Well_Injury = ContentFinder<Texture2D>.Get("UI/Icons/Medical/BandageWell");

        public static readonly Texture2D BleedingIcon_0 = ContentFinder<Texture2D>.Get("bleeding_0");
        public static readonly Texture2D BleedingIcon_1 = ContentFinder<Texture2D>.Get("bleeding_1");
        public static readonly Texture2D BleedingIcon_2 = ContentFinder<Texture2D>.Get("bleeding_2");
        public static readonly Texture2D BleedingIcon_3 = ContentFinder<Texture2D>.Get("bleeding_3");
        public static readonly Texture2D BleedingIcon_4 = ContentFinder<Texture2D>.Get("bleeding_4");
        public static readonly Texture2D BleedingIcon_5 = ContentFinder<Texture2D>.Get("bleeding_5");

        public static readonly Texture2D TendingIcon_0 = ContentFinder<Texture2D>.Get("tending_0");
        public static readonly Texture2D TendingIcon_1 = ContentFinder<Texture2D>.Get("tending_1");
        public static readonly Texture2D TendingIcon_2 = ContentFinder<Texture2D>.Get("tending_2");
        public static readonly Texture2D TendingIcon_3 = ContentFinder<Texture2D>.Get("tending_3");
        public static readonly Texture2D TendingIcon_4 = ContentFinder<Texture2D>.Get("tending_4");
        public static readonly Texture2D TendingIcon_5 = ContentFinder<Texture2D>.Get("tending_5");

        public static readonly Texture2D Bar_Pill = ContentFinder<Texture2D>.Get("bars/pill");
        public static readonly Texture2D Bar_PillBig = ContentFinder<Texture2D>.Get("bars/pill_big");
        public static readonly Texture2D Bar_Ragged = ContentFinder<Texture2D>.Get("bars/ragged");
        public static readonly Texture2D Bar_Infected = ContentFinder<Texture2D>.Get("bars/infected");
        public static readonly Texture2D Bar_DNA = ContentFinder<Texture2D>.Get("bars/dnatwist");
        public static readonly Texture2D Bar_Malnutrition = ContentFinder<Texture2D>.Get("bars/malnutrition");
        public static readonly Texture2D Bar_Cubes = ContentFinder<Texture2D>.Get("bars/cubes");

        // 按 4px 条高预烘焙的 1:1 版本（原图是 16px 高，绘制时被压到 1/4，1.7 的 mip 采样会把它糊掉）。
        // 每张图是原始图案横向重复 16 次：单块像素仍是 1:1（不会被采样糊掉），
        // 但一次 DrawTextureWithTexCoords 就能铺满 176~448px，
        // 平铺所需的 quad 数因此少 16 倍（每帧几千个 quad 是 hediff 一多就卡的主要原因）。
        // 单块原始尺寸：cubes 11x4 / dnatwist 28x4 / infected 13x4 / malnutrition 18x4 / pill 13x4 / pill_big 15x4 / ragged 13x4
        public static readonly Texture2D Bar_Pill_1to1 = ContentFinder<Texture2D>.Get("bars/crisp/pill");
        public static readonly Texture2D Bar_PillBig_1to1 = ContentFinder<Texture2D>.Get("bars/crisp/pill_big");
        public static readonly Texture2D Bar_Ragged_1to1 = ContentFinder<Texture2D>.Get("bars/crisp/ragged");
        public static readonly Texture2D Bar_Infected_1to1 = ContentFinder<Texture2D>.Get("bars/crisp/infected");
        public static readonly Texture2D Bar_DNA_1to1 = ContentFinder<Texture2D>.Get("bars/crisp/dnatwist");
        public static readonly Texture2D Bar_Malnutrition_1to1 = ContentFinder<Texture2D>.Get("bars/crisp/malnutrition");
        public static readonly Texture2D Bar_Cubes_1to1 = ContentFinder<Texture2D>.Get("bars/crisp/cubes");

        static Textures()
        {
            // 1.7 的 IMGUI 通道用固定的 ClampToEdge 采样器，wrapMode 对它无效；
            // 平铺由 CustomHealthCardUtility.DrawBarSegment 手工分块实现。
            // 这里保留 Repeat 只是为了 GetPixel 之类的读取路径和将来走材质渲染时行为一致。
            Bar_Pill.wrapMode = TextureWrapMode.Repeat;
            Bar_PillBig.wrapMode = TextureWrapMode.Repeat;
            Bar_Ragged.wrapMode = TextureWrapMode.Repeat;
            Bar_Infected.wrapMode = TextureWrapMode.Repeat;
            Bar_DNA.wrapMode = TextureWrapMode.Repeat;
            Bar_Malnutrition.wrapMode = TextureWrapMode.Repeat;
            Bar_Cubes.wrapMode = TextureWrapMode.Repeat;
        }
    }
}
