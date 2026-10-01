# Wind and grass interaction

`WindSystem` owns wind direction, speed, force and gusts. The lighting bootstrap installs it for scenes that do not serialize it. `WindPresentation` creates a 256 x 256 half-float slope map and a bounded pool of eight world-space wind ribbons. Both shaders are in Resources so standalone builds retain them.

- `/windline` plays one ribbon in front of the player's camera, including in calm wind. Natural ribbons appear every 2–5 seconds while wind is active.
- Wind-map RG stores signed horizontal slope; B stores its magnitude. The map repeats seamlessly every 512 world units. Its phase advances along wind direction, independently of camera movement. All rendered grass LODs sample the same map and use the same bend function; distance does not weaken wind.
- Collision maps remain separate. RG stores bend direction weighted by pressure, B stores pressure, and A stores gradual bruising. Wheels and registered `GrassInteractionEmitter` objects continue to stamp these maps. Wind never writes collision pressure or bruising.
- Strong pressure turns leaves a muted grey-brown at approximately their original luminance over roughly 0.1–0.4 seconds. Color recovers with the pressure field. The 160-unit near map blends into the 1,200-unit distant map; collision history restores impressions when these windows move. Grass beyond its rendering distance is still culled normally.
- Pressed blades rotate around their roots rather than collapsing to zero height, preserving visible leaf area. GrassFlow also projects the bend along the sampled terrain slope.

Use `WindSystem.force = 0` to test calm conditions. Grass patch wind-strength overrides have been removed so the two rendering paths cannot silently disagree. To test contacts, drive over grass or add `GrassInteractionEmitter` to a moving prop; inspect tracks both nearby and after moving away. EditMode regressions cover calm/direction, ribbon pooling, contact stamping, recovery, history replay and the bruising transition.
