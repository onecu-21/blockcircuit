using System.Numerics;
using Raylib_cs;
using BlockCircuit;

sealed partial class Game
{
    // Minecraft's F5 order: first person, behind the player, then facing the player.
    int perspective;
    Vector3 perspectiveCameraPosition;

    void CyclePerspective()
    {
        perspective = (perspective + 1) % 3;
        Notice(perspective switch
        {
            1 => "Third person: rear view. F5 changes perspective.",
            2 => "Third person: front view. F5 changes perspective.",
            _ => "First person. F5 changes perspective."
        });
    }

    Camera3D BuildCamera(Vector3 eye)
    {
        var position = eye;
        var look = Forward;
        if (perspective != 0)
        {
            var offset = perspective == 1 ? -Forward : Forward;
            position = CameraRig.ResolvePosition(world, eye, offset);
            look = -offset;
        }
        perspectiveCameraPosition = position;
        return new Camera3D
        {
            Position = position,
            Target = position + look,
            Up = Vector3.UnitY,
            FovY = settings.FieldOfView,
            Projection = CameraProjection.Perspective
        };
    }

    void DrawPlayerAvatar(Vector3 interpolatedFeet)
    {
        if (perspective == 0 || Vector3.DistanceSquared(perspectiveCameraPosition,
            interpolatedFeet + new Vector3(0, player.EyeHeight, 0)) < .65f * .65f) return;
        float bodyHeight = player.BodyHeight;
        float legHeight = bodyHeight * .36f;
        float headCenter = Math.Min(bodyHeight - .19f, player.EyeHeight - .04f);
        float torsoTop = headCenter - .19f;
        float torsoHeight = torsoTop - legHeight;
        var coat = new Color(48, 117, 145, 255);
        var trousers = new Color(40, 53, 73, 255);
        var skin = new Color(211, 169, 129, 255);
        Rlgl.PushMatrix();
        try
        {
            Rlgl.Translatef(interpolatedFeet.X, interpolatedFeet.Y, interpolatedFeet.Z);
            Rlgl.Rotatef(yaw * 180f / MathF.PI, 0, 1, 0);
            Raylib.DrawCube(new(-.12f, legHeight / 2, 0), .20f, legHeight, .25f, trousers);
            Raylib.DrawCube(new(.12f, legHeight / 2, 0), .20f, legHeight, .25f, trousers);
            Raylib.DrawCube(new(0, legHeight + torsoHeight / 2, 0), .42f, torsoHeight, .25f, coat);
            Raylib.DrawCube(new(-.29f, legHeight + torsoHeight / 2, 0), .14f, torsoHeight, .22f, coat);
            Raylib.DrawCube(new(.29f, legHeight + torsoHeight / 2, 0), .14f, torsoHeight, .22f, coat);
            Rlgl.Translatef(0, headCenter, 0);
            Rlgl.Rotatef(-pitch * 180f / MathF.PI, 1, 0, 0);
            Raylib.DrawCube(Vector3.Zero, .38f, .38f, .38f, skin);
            Raylib.DrawCube(new(0, .16f, 0), .39f, .08f, .39f, new Color(73, 55, 43, 255));
            Raylib.DrawCube(new(-.09f, .04f, .193f), .045f, .04f, .008f, ink);
            Raylib.DrawCube(new(.09f, .04f, .193f), .045f, .04f, .008f, ink);
        }
        finally { Rlgl.PopMatrix(); }
    }
}
