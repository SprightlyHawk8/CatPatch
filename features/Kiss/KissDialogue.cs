using Together;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace CatPatchspace
{
    internal sealed class KissDialogue
    {
        private static readonly List<string> KissLines = new List<string>
        {
            "...!!",
            "Well... that happened.",
            "Tactical kiss.",
            "We are never explaining this.",
            "Worth it.",
            "This is how rumors start.",
            "Absolutely normal workplace behavior.", // you're not at work
            "Okay, back to surviving.",
            "Mwah!",
            "Charge me, doctor!", // -- ooc
            "I've always wanted to do this...",
            "They would never let us do this back on Earth.",
            "Only on the Grey planet.",
            "I hope no one saw that.",
            "Cutie.",
            "I don't know how to kiss, but I hope that was good enough.",
            "I don't know what to say, but I hope that was good enough.",
            "I hope that was good enough.",
            "My rival, my idol, you got me suicidal, my love, your rage, our dying claim to fame...", // -- ooc
            "You taste really good.",
            "*Slurping noises*", // -- what the fuck
            "Maybe let's stick to hugging for now.",
            "I learned from my handler.", // no you fucking didn't?
            "Last time I tried this, a Dune nearly ripped my jaw off.", // overhauled
            "Uh... no homo?", // you physically can't be gay
            "Oh... we are so gay.", // ok
            "Congrats! You're gay now.", // ok...
            "I'm sorry, Gary.", // -- sorry gary but you're ooc
            "I hope I don't catch the gay!", // ok.
            "MmmmMMhghgf-", // -- the fuck?
            "That... wasn't a hug...",
            "I hope they got this on record!",
            "Platonically kissing you was the best thing ever!",
            "You better write about this in your pod tonight.",
            "We could always just let another group find the cargo.",
            "Our captors can suck my wiggly dick.", // -- i'm fucking killing myself what is this 
            "My handler had a bit too much of a \"hands on\" approach to teaching...", // no they fucking DIDN'T!!!
            "Boop!",
            "What's this, I got it, how could I have forgotten, the physical touch of another, it makes me feel so warm...", // -- ooc
            "My mango, is to blow up, and act like I don't know nobody... what's a mango?", // -- ooc
            "I just drank fent, by the way.", // no you didn't
            "The 10 hour expie kissing scene is plot relevant, I swear!", // -- ooc
            "With the power of yaoi, we shall prevail!", // doesn't make sense
            "With the power of yuri, we shall prevail!", // ditto
            "Wanna share a syringe after this?", // what the fuck? overhauled
            "I saw this on a blueprint once.",
            "I don't know if we should take this any further...",
            "We should stop at this...",
            "This is like that one gif.", // -- ooc
            "What's my gender? kiss me again and find out.", // -- this doesn't make any fucking sense
            "I assume this means you'll be treat me to dinner.",
            "A little less tongue next time, yeah?",
            "If anyone asks, yes, we're a thing now.",
            "Fine... I guess I'll be your little pog champ.",
            "I KNEW YOU WERE GAY ALL ALONG!", // -- they have no gender
            "I guess I'll endure your affection just this once.",
            "LESS TEETH, YOU BUGGY FUCK!", // -- ooc? overhauled
            "Yummers.",
            "34 rules about to be violated tonight!",
            "Call me a spike stabber the way I... Fuck, I forgot, nevermind.",
            "They're gonna have to rename this to the freakpatch.", // -- ooc
            "vaporeonx5 was here :3c", // -- ooc
            "I guess I'm trans now..?", // -- that doesnt make any sense at all
            "Oh yeah, keep jorking me, oh yeah ong ong", // -- what the fuck
            "Orsoniks approved mod!", // -- ooc

            // overhaul
            "I think... I liked that.",
            "...oh?",
            "Last time I tried this, they nearly ripped my jaw off.", // overhaul
            "Uhh...",
            "That's... nice?",
            "Are you, um...",
            "Is this okay..?",
            "Mmmh...",
            "How affectionate...",
            "What a display of affection...",
            "Ah! Mmhh... Thank you...",
            "Aw! You're so nice!",
            "Less- teeth, gosh.", // overhaul
            "Still platonic, right?",
            "I hope there aren't any cameras around here.", // "i hope they got this on record!"
            "I think I skipped the kissing course...",
            "You're cute...",
            "You're warm...",
            "You're soft...",
            "Oh, uh..! Is this alright?-",
            "Did you just eat something..?", // "i just drank fent btw"
            "Is this relevant to the mission..?",
            "I feel better...",
            "Wanna share a burger after this?", // a syringe is cringe and yucky
            "Are we a thing now..?",
            "Your tail is wagging.",
            "Haha- is my tail wagging?",
            "You're just licking my face... okay...",
            "Uh- what rule did we violate with this?", // "34 rules about to be violated tn"
            "Haha, you okay?",
            "My heart is gonna jump out of my chest...",

        };

        public string ChooseLine()
        {
            if (KissLines.Count == 0)
                return "";

            return KissLines[Random.Range(0, KissLines.Count)];
        }

        public void PlayLine(knetid speakerClientId, string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return;

            ScavPlayer speaker;
            Body body;
            if (!ScavPlayer.TryGetPlayerAndBodyFromClientId(speakerClientId, out speaker, out body) || body == null)
                return;

            TrySayExactLine(body, line);
        }

        public void PlayLineDelayed(knetid speakerClientId, string line, float delaySeconds)
        {
            if (string.IsNullOrWhiteSpace(line))
                return;

            if (Plugin.Instance == null || delaySeconds <= 0f)
            {
                PlayLine(speakerClientId, line);
                return;
            }

            Plugin.Instance.StartCoroutine(PlayLineAfterDelay(speakerClientId, line, delaySeconds));
        }

        private IEnumerator PlayLineAfterDelay(knetid speakerClientId, string line, float delaySeconds)
        {
            yield return new WaitForSeconds(delaySeconds);
            PlayLine(speakerClientId, line);
        }

        private static void TrySayExactLine(Body body, string line)
        {
            if (body == null || !body.alive || !body.conscious)
                return;

            Talker talker = body.talker;
            if (talker == null)
                talker = body.GetComponent<Talker>();

            if (talker == null)
                return;

            Talker_Talk_MultiplayerPatch.Force(
                talker,
                new List<string> { line },
                null,
                true,
                true);
        }
    }
}
