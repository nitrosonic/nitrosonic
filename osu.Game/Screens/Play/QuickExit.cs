﻿// Copyright (c) 2007-2018 ppy Pty Ltd <contact@ppy.sh>.
// Licensed under the MIT Licence - https://raw.githubusercontent.com/ppy/osu/master/LICENCEusing System;

using osu.Framework.Input.Bindings;
using osu.Game.Input.Bindings;
using osu.Game.Overlays;

namespace osu.Game.Screens.Play
{
    public class QuickExit : HoldToConfirmOverlay, IKeyBindingHandler<GlobalAction>
    {
        public bool OnPressed(GlobalAction action)
        {
            if (action != GlobalAction.QuickExit) return false;

            BeginConfirm();
            return true;
        }

        public bool OnReleased(GlobalAction action)
        {
            if (action != GlobalAction.QuickExit) return false;

            AbortConfirm();
            return true;
        }
    }
}
