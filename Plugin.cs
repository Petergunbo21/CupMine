
Sprite MakePlayerFrame(int view, int step, int build, int flame)
{
    // 16 x 36 pixel-art sprite.
    // view: 0 = front, 1 = back, 2 = side.
    var p = new Pix(16, 36);

    Color32 skin       = C(198, 153, 108);
    Color32 skinShade  = C(157, 112, 76);
    Color32 hair       = C(83, 54, 32);
    Color32 hairLight  = C(112, 76, 43);
    Color32 shirt      = C(45, 111, 210);
    Color32 shirtShade = C(30, 76, 160);
    Color32 pants      = C(54, 64, 158);
    Color32 pantsShade = C(36, 43, 112);
    Color32 boot       = C(70, 48, 31);
    Color32 eyeWhite   = C(240, 240, 235);
    Color32 eye        = C(42, 107, 177);
    Color32 toolWood   = C(126, 78, 40);
    Color32 toolLight  = C(180, 185, 190);
    Color32 toolDark   = C(105, 112, 120);

    // Walking animation.
    int leftLift = step == 1 ? 2 : 0;
    int rightLift = step == 2 ? 2 : 0;

    // Legs: two block-shaped legs and square boots.
    p.Rect(3, leftLift, 5, 10 - leftLift, pants, 0);
    p.Rect(8, rightLift, 5, 10 - rightLift, pants, 0);
    p.Rect(3, leftLift + 3, 5, 2, pantsShade, 0);
    p.Rect(8, rightLift + 3, 5, 2, pantsShade, 0);
    p.Rect(3, leftLift, 5, 2, boot, 0);
    p.Rect(8, rightLift, 5, 2, boot, 0);

    // Torso: blue shirt with shaded edges.
    p.Rect(3, 10, 10, 11, shirt, 0);
    p.Rect(3, 10, 2, 10, shirtShade, 0);
    p.Rect(11, 10, 2, 10, shirtShade, 0);
    p.Rect(6, 12, 4, 6, shirt, 0);

    // Arms.
    int armSwing = step == 1 ? 1 : (step == 2 ? -1 : 0);
    p.Rect(0, 11 + armSwing, 3, 8, shirt, 0);
    p.Rect(13, 11 - armSwing, 3, 8, shirt, 0);
    p.Rect(0, 11 + armSwing, 3, 2, skin, 0);
    p.Rect(13, 11 - armSwing, 3, 2, skin, 0);

    // Square head.
    p.Rect(2, 20, 12, 12, skin, 0);

    // Hair cap and side hair.
    p.Rect(2, 29, 12, 4, hair, 0);
    p.Rect(2, 26, 2, 5, hair, 0);
    p.Rect(12, 26, 2, 5, hair, 0);
    p.Rect(4, 30, 3, 2, hairLight, 0);
    p.Rect(8, 30, 3, 2, hairLight, 0);

    if (view == 0)
    {
        // Steve's front-facing pixel eyes.
        p.Rect(4, 25, 3, 2, eyeWhite, 0);
        p.Rect(9, 25, 3, 2, eyeWhite, 0);
        p.Rect(5, 25, 2, 2, eye, 0);
        p.Rect(9, 25, 2, 2, eye, 0);

        // Nose and mouth/beard shading.
        p.Rect(7, 22, 2, 3, skinShade, 0);
        p.Rect(6, 21, 4, 1, hair, 0);
        p.Rect(7, 21, 2, 1, skinShade, 0);
    }
    else if (view == 1)
    {
        // Back of head: mostly brown hair.
        p.Rect(2, 20, 12, 12, hair, 0);
        p.Rect(4, 25, 3, 4, hairLight, 0);
        p.Rect(9, 23, 3, 5, hairLight, 0);
    }
    else
    {
        // Side view, facing right.
        p.Rect(2, 29, 12, 4, hair, 0);
        p.Rect(2, 25, 3, 6, hair, 0);
        p.Rect(9, 25, 3, 2, eyeWhite, 0);
        p.Rect(10, 25, 2, 2, eye, 0);
        p.Rect(12, 22, 2, 3, skinShade, 0);
    }

    // Building pose: raise the right arm.
    if (build == 1)
    {
        p.Rect(13, 17, 3, 5, shirt, 0);
        p.Rect(13, 22, 3, 3, skin, 0);
    }
    else if (build == 2)
    {
        p.Rect(13, 12, 3, 7, shirt, 0);
        p.Rect(13, 10, 3, 3, skin, 0);
    }

    // PIXEL PICKAXE.
    // Drawn after the body so it appears in front of the hand.
    // Handle.
    p.Rect(13, 17, 2, 9, toolWood, 0);
    p.Rect(14, 18, 1, 7, C(145, 94, 49), 0);

    // Pickaxe head: stepped, blocky metal shape.
    p.Rect(8, 25, 7, 2, toolDark, 0);
    p.Rect(9, 27, 5, 2, toolLight, 0);
    p.Rect(10, 29, 3, 1, toolLight, 0);
    p.Rect(8, 26, 2, 2, toolLight, 0);
    p.Rect(14, 26, 2, 2, toolDark, 0);

    // Small wooden connection between head and handle.
    p.Rect(12, 24, 2, 3, toolWood, 0);

    return Sprite.Create(
        p.ToTexture(),
        new Rect(0, 0, 16, 36),
        new Vector2(0.5f, 0f),
        16f
    );
}
