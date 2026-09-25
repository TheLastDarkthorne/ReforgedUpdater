using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ReforgedUpdater.Gui
{
    public enum MascotMood
    {
        Idle,       // nothing to report
        Happy,      // everything is current
        Excited,    // updates are waiting
        Busy,       // checking or downloading
        Oops        // something went wrong
    }

    /// <summary>Belora: shows how things are going at a glance, and bounces while it works.</summary>
    public partial class Mascot : UserControl
    {
        public static readonly DependencyProperty MoodProperty = DependencyProperty.Register(
            nameof(Mood), typeof(MascotMood), typeof(Mascot),
            new PropertyMetadata(MascotMood.Idle, (d, e) => ((Mascot)d).ApplyMood()));

        public MascotMood Mood
        {
            get => (MascotMood)GetValue(MoodProperty);
            set => SetValue(MoodProperty, value);
        }

        public Mascot()
        {
            InitializeComponent();
            Loaded += (s, e) => { StartIdleLoops(); ApplyMood(); };
        }

        private void StartIdleLoops()
        {
            // A slow breath, so it never looks frozen.
            var breathe = new DoubleAnimation(1, 1.035, TimeSpan.FromSeconds(1.7))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            Squish.BeginAnimation(ScaleTransform.ScaleYProperty, breathe);
            Squish.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(1, 0.985, TimeSpan.FromSeconds(1.7))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                });

            // Blink every few seconds.
            var blink = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, Duration = TimeSpan.FromSeconds(4.3) };
            blink.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            blink.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(3.9))));
            blink.KeyFrames.Add(new LinearDoubleKeyFrame(0.1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(4.0))));
            blink.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(4.12))));
            Blink.BeginAnimation(ScaleTransform.ScaleYProperty, blink);
        }

        private void ApplyMood()
        {
            var mood = Mood;

            EyesOpen.Visibility = Show(mood == MascotMood.Idle || mood == MascotMood.Excited || mood == MascotMood.Busy);
            EyesHappy.Visibility = Show(mood == MascotMood.Happy);
            EyesOops.Visibility = Show(mood == MascotMood.Oops);

            MouthSmile.Visibility = Show(mood == MascotMood.Idle);
            MouthOpen.Visibility = Show(mood == MascotMood.Happy || mood == MascotMood.Excited);
            MouthO.Visibility = Show(mood == MascotMood.Busy);
            MouthWobble.Visibility = Show(mood == MascotMood.Oops);
            Sweat.Visibility = Show(mood == MascotMood.Oops);
            Sparkles.Visibility = Show(mood == MascotMood.Happy || mood == MascotMood.Excited);

            if (mood == MascotMood.Busy)
            {
                var hop = new DoubleAnimation(0, -8, TimeSpan.FromSeconds(0.3))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                Hop.BeginAnimation(TranslateTransform.YProperty, hop);

                var shrink = new DoubleAnimation(1, 0.75, TimeSpan.FromSeconds(0.3))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                ShadowScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
            }
            else if (mood == MascotMood.Excited || mood == MascotMood.Happy)
            {
                // One happy little jump, then settle.
                var jump = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(0.7) };
                jump.KeyFrames.Add(new EasingDoubleKeyFrame(-10, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.22)),
                    new QuadraticEase { EasingMode = EasingMode.EaseOut }));
                jump.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.5)),
                    new BounceEase { Bounces = 1, Bounciness = 3, EasingMode = EasingMode.EaseOut }));
                Hop.BeginAnimation(TranslateTransform.YProperty, jump);
                ShadowScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            }
            else
            {
                Hop.BeginAnimation(TranslateTransform.YProperty, null);
                ShadowScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            }

            if (Sparkles.Visibility == Visibility.Visible)
            {
                Twinkle(Spark1Scale, 0);
                Twinkle(Spark2Scale, 0.6);
            }
        }

        private static void Twinkle(ScaleTransform target, double delaySeconds)
        {
            var twinkle = new DoubleAnimation(1, 0.35, TimeSpan.FromSeconds(0.8))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = TimeSpan.FromSeconds(delaySeconds),
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            target.BeginAnimation(ScaleTransform.ScaleXProperty, twinkle);
            target.BeginAnimation(ScaleTransform.ScaleYProperty, twinkle);
        }

        private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
    }
}
