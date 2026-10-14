using Together;
using CasualtiesTogetherUtils;
using System.Collections.Generic;
using System.Numerics;
using UnityEngine;
using static UnityEngine.ParticleSystem.PlaybackState;

namespace CatPatchspace
{
    internal sealed class TalkerCatPatch
    {
        private static readonly List<string> VulnerableHugLines = new List<string>
        {
            "Thanks... I needed that.",
            "Can we stay like this for a second?",
            "I'm not doing great.",
            "I'm so tired...",
            "I don't think I've felt okay for a while.",
            "I don't want to... I can't be alone right now.",
            "I'm not a burden, am I?",
            "This helps... a little...",
            "I want to cry...",
            "Everything feels wrong lately.",
            "I needed a reminder that somebody still cares.",
            "I've been holding everything in.",
            "I was starting to spiral again.",
            "Our captors will get what they fucking deserve.",
            "I think I might consider a mindwipe.",
            "I dreamt about our captors again last night... it was so vivid...", // most people don't sleep in mpmod
            "What did we do to deserve this?",
            "I hate how much this place affects me.",
            "I'm scared of what I'm becoming in here.",
            "When I get my claws on them, they will know pain.", // ehhh
            "I don't even recognize myself anymore.",
            "I'm really scared...",
            "I can't lose you, too...",
            "Please don't leave me...",
            "I've lost so many... I can't lose you too.",
            "I'm trying really hard to keep going.",
            "There is no honest way out, huh.",
            "...",

            //AnUncreativeName
            "If something happens, please don’t forget about me.",

            // Devactor
            "Will I even make it out..?",
            "I hate this... I really do...",
            "They don't know what I'm going through down here...",
            "Is this what we are to them..?",
            "This only delays the inevitable...",
            "I can't die like this...",
            "Are... Are we going to make it out..?",
            "Please tell me we going to be okay...",
            "Their salvation will be rotten...", // what?
            "I don't think this is enough.. but thank you.",
            "Please don't leave my side... at least until I'm okay.",
            "Will they toss us aside if we make it out? What will they do to us..?",
            "I... I don't want to do this anymore...",
            "I want to go somewhere else... anywhere but here...",
            "I... Won't end up like the rest, right..?",
            "Their bodies... mangled... stabbed... cold... why..?", // ehh
            "I can't... keep going...",
            "I don't want to end up like them...",
            "Why are there so many of us down here..? So many... all dead.", 
            "I can't stop crying...",
            "I can't stop looking at the ground...", // ehhh
            "I'm scared of this place...",
            "They won't get away with this.. I'll... I don't know...",
            "What will we do once this is all over..?",
            "I hate that I'm actually adapting to this place...",
            "The traps... the lights... the corpses...", // what lights
            "Why can't they just get it themselves instead of commiting a genocide..?",
            "I'm sick of this place... I'm sick of it all.",
            "I feel like I'm not going to make it...",
            "I won't fall victim like the rest, right..?", // what?
            "I want to wake up...",
            "This is all a bad dream, right..?",
            "Why would they... why...",
            "I... please don't watch if I scratch myself...",
            "You shouldn't care about someone as useless as me...",
            "I'm just dead weight, aren't I..?",
            "I'm not smart like a Milky and I'm not strong like a Dune... why bother...", // -- overhauled this and left it generally the same so i'm noting this one instead
            "I hate this place... why here of all places...", // eh
            "If this keeps up I might have to resort to my own way out of this place...", // ehhh

            //Novartis
            "...I wish we could have stayed...",
            "Just this once...",

            // overhaul

            "I can't even look up...",
            "I'm not smart and I'm not strong... why bother...",
            "If this keeps up I might have to...",
            "I'm exhausted.",
            "I'm so sick of this place.",
            "I hate this place.",
            "I hate myself...",
            "Please tell me we're going to be okay.",
            "I'm trying... I'm trying so hard...",
            "I'm trying not to cry, okay..?",
            "I don't want to die...",
            "I couldn't care less about the cargo.",
            "Thank you... Thank you...",
            "...Okay...",

        };

        private static readonly List<string> ComfortHugLines = new List<string>
        {
            "You looked like you needed this.",
            "It's okay, even thought it's not, just know I'll be here for you.",
            "You've been carrying too much alone.",
            "You know I care about you.",
            "Hey, hey, it's okay. I'm here.",
            "Come here...",
            "If you need to cry, I understand. I'm here for you.",
            "I'll be here for you, no matter what.",
            "If you need a shoulder to cry on, I'm here.",
            "Don't worry, I've got you.",
            "I know that look. I've had it too.",
            "Just rest your head for a second.",
            "I hope our captors are burning in hell for this.",
            "Our captors can go fuck themselves.",
            "Our captors can go to hell.",
            "Fuck 'em.",
            "When we get back, we will make them pay, don't worry.",
            "You don't have to handle this alone.",
            "Don't let this place win.",
            "You shouldn't have to go through this alone.",
            "Hey... breathe, alright? I'm here now.",
            "Smoke 'em if you got 'em...", // -- what? cigs mod?
            "I’m not gonna leave you. I’m right here.",
            "We are in this together, remember?",
            "I know it’s hard, but we can get through this. Together.",
            "I’m not going anywhere. I’ve got you.",
            "I know you're scared, but we'll make it through this. I promise.",
            "I wish I could make it all better, but I can’t. I care about you and I’m here for you.",
            "I know this place is awful, but you’re not alone. I’m here with you.",
            "You’re doing the best you can, and that’s all anyone can ask of you. I’m proud of you.",
            "You are so strong, I'm so proud of you.",
            "I know it feels hopeless, but we have to keep going.",
            "You are the strongest one here.",
            "I'm so proud of you.",
            //ray of sunshine
            "Shh... Just hold on...", 
            "I’m by your side, buddy, it’s gonna be okay...", 
            "None of this is your fault. Not any of this.", 
            "Just keep breathing, slowly. You’re doing good...", 
            //desk
            "It’s going to be okay... everything's going to be okay...",
            //AnUncreativeName
            "Let's not give up just yet.",
            "Despite everything we’re still here, right?",
            "I won’t let you get hurt again.",

            // overhaul 

            "You're okay.",
            "You'll be fine...",
            "Come here.",
            "It's gonna be okay...",
            "I'm proud of you.",
            "You're gonna make it out.",
            "I love you, it'll be okay.",
            "You're not alone.",
            "I'll stay near.",
            "Here, you're okay...",
            "It's all good...",
            "I'm here, it'll be okay..."

        };

        private static readonly List<string> CheerfulHugLines = new List<string>
        {
            "Hug time!",
            "Charge me, doctor!", // -- ooc 
            "Come here!",
            "This is nice.",
            "There are so many cool things here! I will never go back to the lab!",
            "You deserve a hug.",
            "I think you are amazing!",
            "You are like, so totally awesome.",
            "I hope things stay like this.",
            "Wasn't this supposed to be hard and depressing?",
            "We got this in the bag.",
            "Yippee!",
            "Physical touch makes me feel all tingly.",
            "I'm gonna touch you >:3", // -- okay
            "So like... wanna make out?", // -- ew
            "We are doing SO good.",
            "I like your fur.",
            "This is WAY better than dying alone.",
            "Okay maybe life IS worth living.",
            "We're gonna make it. Probably.",
            "This almost makes me forget the horrors.",
            "You are officially my emotional support survivor.",
            "I cast emotional support!",
            "We're doing great, sweetie!",
            "Let's do this again later!",
            "This place can't stop me from being silly!",
            "Wanna go blow up a life pod later?", // -- that's ehhhh humor
            "When we get back, let's escape and go on a roadtrip!",
            "The horrors persist but so do we!",
            "We're doing amazing, sweetie.",
            "We're so back.",
            "Are you a blood bag? Because I feel empty without you.",
            "...!!!",
            "I think we deserve a little whimsy.",
            "We should do this before every near-death experience.",
            "If we die horribly later, this was still worth it.",
            "Wow. Serotonin. Haven't felt that since my handler passed away.", // -- do they really get "handlers" . there are thousands if not millions of sawians
            "I'm resisting the urge to start spinning in circles.",
            "I'm choosing to believe everything will be okay.",
            "Look at us. Surviving and everything.",
            "I'm full of dangerous amounts of optimism.",
            "We are absolutely crushing this apocalypse thing.",
            "Morale has increased by approximately five hundred percent.",
            "Your fur is SO soft, wow.",
            "happy_responce_dialogie_option_27", // -- eh
            "*Happiness noises*", // -- whatever
            //ray of sunshine
            "I wonder if I could suplex you.",
            "Can you not crush me? Thanks.",
            "Your breath stinks.", // -- thats not nice
            "...You smell like steak. Maybe I’m just hungry.",
            //AnUncreativeName
            "So... Come here often?",
            "I wasn’t expecting that. I didn’t mean stop!",
            "Oh nooooo, please don’t hug me agaiinnnnnnnn...", // -- eh
            "I’m proud of who we’ve become. Let’s be even better!",
            //Hazel Wander
            "Imagine if we could just LIVE after this, no experiments, no tests.",
            //Novartis
            "We should totally cuddle.",
            "I love this... wait a minute-",

            // overhaul

            "We're gonna make it!",
            "Oh, I love this place!",
            "Yawn... you're really warm, y'know?",
            "Oof- didn't expect this, haha!",
            "We should do this more!",
            "Awwh...",
            "We're doing amazing!",
            "Doing great so far!",
            "C'mere, you!",
            "There, there.",
            "Oof- hey, you're crushing me! Haha!",
            "Wanna share some food later?", // -- no i dont wanna blow up lifepods ! lets do nice things !
            "You're so fluffy!",
            "You're really soft...",
            "Thank you!",
            "Thanks...",
            "Wow, this is nice...",
            "Let's do this more often."

        };

        private static readonly List<string> NeutralHugLines = new List<string>
        {
            "There you go.",
            "Alright, bring it in.",
            "Our captors would be so mad at us right now.",
            "No wonder this was agianst the rules.",
            "This is nice.",
            "Thanks.",
            "Could be worse, right?",
            "Can't wait to fuck up our captors!",
            "This makes me feel a little less alone.",
            "Hope you are doing well.",
            "Is this how you thought we were going to end up?",
            "Crazy times we're living in.",
            "I like this.",
            "Don't make it weird.",
            "You're warmer than I expected.",
            "This almost feels normal.",
            "This place really wears us down, huh.",
            "So, what did they put you through back on Earth?",
            "We keep going.",
            "One step at a time.",
            "We got this.",
            "If I lose you too, I might go insane.",
            "I ever tell you about the time my buddy Keith told me he was picked up by some little green men and they--", // -- no and i do not give a shit
            "Not too bad, right?",
            "I really do not miss being beaten...",
            "Our captors are probably really upset at us.",
            "I watched my buddy get sawed in half on Earth, so somehow this is a step up.", // -- why would they do that? stupid waste of resources
            "I don't know about you, but I needed that.",
            "I ever tell you about the time my buddy Keith fell down an open manhole? He was unconscious down there for like a week. During that time, unbeknownst to Keith, they paved over him. Keith had to--", // -- keith has to die
            "Can't hurt to slow down for a second.",
            "I enjoy your company.",
            "Just making sure you're still with me.",
            "Don't get used to this.",
            "You're tougher than you look.",
            "One decent moment in this mess.",
            "Just be careful, stick together, and don't die.",
            "Could be worse, honestly.",
            "I'd miss you if you died, so don't do that.",
            "Being alone would suck down here.",
            "Better than dying alone, right?",
            "Not dead yet.",
            "I'm glad you're alive.",
            "Hang in there.",
            "Don't give up.",
            "You're softer than the others.",
            "Just trying to keep morale up.",
            "You're doing alright.",
            "This place gets lonely fast. I appreciate you.",
            "I appreciate you.",
            "What do you think Earth is like?", // -- why ask? you literally were there
            "Two can be one...",
            "Well, now we both need a shower.", //AnUncreativeName inspired
            //ray of sunshine
            "Yeah, okay. Bring it in.",
            "I kind of expected something else...", // -- what?
            //AnUncreativeName
            "Let me get that for you.",
            "We’ve got places to be, but that can wait.",
            //Novartis
            "Just making sure.",
            "Uh... I slipped?",

            // overhaul 

            "Soft...",
            "You're fluffy.",
            "Thanks... I feel better.",
            "Cute...",
            "Warm...",
            "Hey, this is nice...",
            "I feel better.",
            "Thank you!",
            "Lovely.",
            "Nice!",
            "Hug time!",
            "Oh, this feels amazing...",
            "Do you think the others are okay?", // -- could be both the "other players" and "the others back at the labs" so that it works even in 2p runs
            "I'd miss you if you died, y'know?",
            "We're doing okay.",
            "Doing well so far...",
            "Not too bad.",
            "We're okay, aren't we?",
            "You okay?",
            "All good?"


        };

        public string ChooseHugAcceptedLine(Body speakerBody, Body receiverBody)
        {
            List<string> lines = GetHugLinesForMood(speakerBody, receiverBody);

            if (lines == null || lines.Count == 0)
                return "";

            return lines[Random.Range(0, lines.Count)];
        }

        public void PlayHugAcceptedLine(knetid speakerClientId, string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return;

            ScavPlayer speaker;
            Body speakerBody;

            if (!ScavPlayer.TryGetPlayerAndBodyFromClientId(speakerClientId, out speaker, out speakerBody))
                return;

            TrySayExactLine(speakerBody, line);
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

        private static List<string> GetHugLinesForMood(Body speakerBody, Body receiverBody)
        {
            if (speakerBody != null && speakerBody.totalHappiness <= -40f)
                return GetHugLines("catpatch_hug_vulnerable", speakerBody, VulnerableHugLines);

            if (receiverBody != null && receiverBody.totalHappiness <= -40f)
                return GetHugLines("catpatch_hug_comfort", speakerBody, ComfortHugLines);

            if (speakerBody != null && speakerBody.totalHappiness >= 40f)
                return GetHugLines("catpatch_hug_cheerful", speakerBody, CheerfulHugLines);

            return GetHugLines("catpatch_hug_neutral", speakerBody, NeutralHugLines);
        }

        private static List<string> GetHugLines(string localeKey, Body body, List<string> fallbackLines)
        {
            List<string> localeLines = TryGetLocaleHugLines(localeKey, body);

            if (localeLines != null && localeLines.Count > 0 && !IsMissingLocaleFallback(localeLines, localeKey))
                return localeLines;

            return fallbackLines;
        }

        private static List<string> TryGetLocaleHugLines(string localeKey, Body body)
        {
            try
            {
                int characterType = body != null ? body.charType : 0;
                return Locale.GetCharacter(localeKey, characterType);
            }
            catch
            {
                return null;
            }
        }

        private static bool IsMissingLocaleFallback(List<string> lines, string localeKey)
        {
            return lines.Count == 1 && lines[0] == localeKey;
        }
    }
}
