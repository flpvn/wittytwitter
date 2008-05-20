﻿using System;
using System.ComponentModel;
using System.Deployment.Application;
using System.Media;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Snarl;
using TwitterLib;
using TwitterLib.Utilities;
using Witty.ClickOnce;
using Witty.Properties;

namespace Witty
{
    public partial class MainWindow
    {

        private IntPtr SnarlConfighWnd;
        private bool reallyexit = false;

        public MainWindow()
        {
            this.InitializeComponent();

#if DEBUG
            Title = Title + " Debug";
#endif

            // Trap unhandled exceptions
            LayoutRoot.Dispatcher.UnhandledException += new DispatcherUnhandledExceptionEventHandler(Dispatcher_UnhandledException);

            #region Minimize to tray setup

            _notifyIcon = new System.Windows.Forms.NotifyIcon();
            _notifyIcon.BalloonTipText = "Right-click for more options";
            _notifyIcon.BalloonTipTitle = "Witty";
            _notifyIcon.Text = "Witty - The WPF Twitter Client";
            _notifyIcon.Icon = Witty.Properties.Resources.AppIcon;
            _notifyIcon.DoubleClick += new EventHandler(m_notifyIcon_Click);

            System.Windows.Forms.ContextMenu notifyMenu = new System.Windows.Forms.ContextMenu();
            System.Windows.Forms.MenuItem openMenuItem = new System.Windows.Forms.MenuItem();
            System.Windows.Forms.MenuItem exitMenuItem = new System.Windows.Forms.MenuItem();

            notifyMenu.MenuItems.AddRange(new System.Windows.Forms.MenuItem[] { openMenuItem, exitMenuItem });
            openMenuItem.Index = 0;
            openMenuItem.Text = "Open";
            openMenuItem.Click += new EventHandler(openMenuItem_Click);
            exitMenuItem.Index = 1;
            exitMenuItem.Text = "Exit";
            exitMenuItem.Click += new EventHandler(exitMenuItem_Click);

            _notifyIcon.ContextMenu = notifyMenu;
            this.Closed += new EventHandler(OnClosed);
            this.StateChanged += new EventHandler(OnStateChanged);
            this.IsVisibleChanged += new DependencyPropertyChangedEventHandler(OnIsVisibleChanged);

            // used to override closings and minimize instead
            this.Closing += new CancelEventHandler(MainWindow_Closing);

            #endregion

            #region Single instance setup
            // Enforce single instance for release mode
#if !DEBUG
            Application.Current.Exit += new ExitEventHandler(Current_Exit);
            _instanceManager = new SingleInstanceManager(this, ShowApplication);
#endif
            #endregion

            // Set the data context for all of the tabs
            LayoutRoot.DataContext = tweets;
            RepliesListBox.ItemsSource = replies;
            UserTab.DataContext = userTweets;
            MessagesListBox.ItemsSource = messages;

            // Set how often to get updates from Twitter
            refreshInterval = new TimeSpan(0, int.Parse(AppSettings.RefreshInterval), 0);

            this.Topmost = AlwaysOnTopMenuItem.IsChecked = AppSettings.AlwaysOnTop;

            // Does the user need to login?
            if (string.IsNullOrEmpty(AppSettings.Username))
            {
                PlayStoryboard("ShowLogin");
            }
            else
            {
                LoginControl.Visibility = Visibility.Hidden;

                System.Security.SecureString password = TwitterNet.DecryptString(AppSettings.Password);
                twitter = new TwitterNet(AppSettings.Username, password, WebProxyHelper.GetConfiguredWebProxy());

                // Let the user know what's going on
                StatusTextBlock.Text = Properties.Resources.TryLogin;
                PlayStoryboard("Fetching");

                // Create a Dispatcher to attempt login on new thread
                NoArgDelegate loginFetcher = new NoArgDelegate(this.TryLogin);
                loginFetcher.BeginInvoke(null, null);
            }

            InitializeClickOnceTimer();

            InitializeSoundPlayer();

            ScrollViewer.SetCanContentScroll(TweetsListBox, !AppSettings.SmoothScrolling);

            //Register with Snarl if available
            if (SnarlInterface.SnarlIsActive())
            {
                //We Create a Message Only window for communication
                this.SnarlConfighWnd = Win32.CreateWindowEx(0, "Message", null, 0, 0, 0, 0, 0, new IntPtr(Win32.HWND_MESSAGE), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                SnarlInterface.RegisterConfig("Witty", this.SnarlConfighWnd.ToInt32(), "");
                SnarlInterface.RegisterAlert("Witty", "New Tweet");
            }
        }

        void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            // If the user selected to minimize on close and the window state is normal
            // just minimize the app
            if (AppSettings.MinimizeOnClose && this.reallyexit == false)
            {
                e.Cancel = true;
                _storedWindowState = this.WindowState;
                this.WindowState = WindowState.Minimized;
                if (_notifyIcon != null)
                {
                    _notifyIcon.ShowBalloonTip(2000);
                }
            }
        }

        #region Fields and Properties

        // Main collection of tweets
        private TweetCollection tweets = new TweetCollection();

        // Main collection of replies
        private TweetCollection replies = new TweetCollection();

        private DateTime repliesLastUpdated;

        // Main collection of user Tweets
        private TweetCollection userTweets = new TweetCollection();

        // Main collection of direct messages
        private DirectMessageCollection messages = new DirectMessageCollection();

        private DateTime messagesLastUpdated;

        // Main TwitterNet object used to make Twitter API calls
        private IServiceApi twitter;

        // Timer used for automatic tweet updates
        private DispatcherTimer refreshTimer = new DispatcherTimer();

        // How often the automatic tweet updates occur.  TODO: Make this configurable
        private TimeSpan refreshInterval;

        // Delegates for placing jobs onto the thread dispatcher.  
        // Used for making asynchronous calls to Twitter so that the UI does not lock up.
        private delegate void NoArgDelegate();
        private delegate void OneArgDelegate(TweetCollection arg);
        private delegate void OneStringArgDelegate(string arg);
        private delegate void AddTweetUpdateDelegate(Tweet arg);
        private delegate void MessagesDelegate(DirectMessageCollection arg);
        private delegate void SendMessageDelegate(string user, string text);
        private delegate void LoginDelegate(User arg);
        private delegate void DeleteTweetDelegate(double id);

        // Settings used by the application
        private Properties.Settings AppSettings = Properties.Settings.Default;

        // booleans to keep track of state
        private bool isExpanded;
        private bool isLoggedIn;
        private bool isMessageExpanded;

        private enum CurrentView
        {
            Recent, Replies, User, Messages
        }

        private CurrentView currentView
        {
            get
            {
                switch (Tabs.SelectedIndex)
                {
                    case 0:
                        return CurrentView.Recent;
                    case 1:
                        return CurrentView.Replies;
                    case 2:
                        return CurrentView.User;
                    case 3:
                        return CurrentView.Messages;
                    default:
                        return CurrentView.Recent;
                }
            }
        }

        private string displayUser;

        private Deployment _clickOnce;
        private System.Windows.Threading.DispatcherTimer _clickOnceUpdateTimer;

        private int popupCount = 0;

        internal Tweet SelectedTweet
        {
            get
            {
                Tweet selectedTweet = null;
                if (this.currentView == CurrentView.Replies)
                {
                    if (null != RepliesListBox.SelectedItem) selectedTweet = (Tweet)RepliesListBox.SelectedItem;
                }
                else if (this.currentView == CurrentView.Messages)
                {
                    if (null != MessagesListBox.SelectedItem) selectedTweet = ((DirectMessage)MessagesListBox.SelectedItem).ToTweet();
                }
                else
                {
                    if (null != TweetsListBox.SelectedItem) selectedTweet = (Tweet)TweetsListBox.SelectedItem;
                }
                return selectedTweet;
            }
        }

        SoundPlayer _player; 
        #endregion

        #region Retrieve new tweets

        /// <summary>
        /// Encapsulated method to create dispatcher for fetching new tweets asynchronously
        /// </summary>
        private void DelegateRecentFetch()
        {
            // Let the user know what's going on
            StatusTextBlock.Text = "Retrieving tweets...";

            PlayStoryboard("Fetching");

            // Create a Dispatcher to fetching new tweets
            NoArgDelegate fetcher = new NoArgDelegate(
                this.GetTweets);

            fetcher.BeginInvoke(null, null);
        }

        private void Timer_Elapsed(object sender, EventArgs e)
        {
            DelegateRecentFetch();
        }

        private void GetTweets()
        {
            try
            {
                // Schedule the update function in the UI thread.
                LayoutRoot.Dispatcher.BeginInvoke(
                    DispatcherPriority.Normal,
                    new OneArgDelegate(UpdateUserInterface), twitter.GetFriendsTimeline());
            }
            catch (WebException ex)
            {
                App.Logger.Debug(String.Format("There was a problem fetching new tweets from Twitter.com: {0}", ex.ToString()));
            }
        }

        private void UpdateUserInterface(TweetCollection newTweets)
        {
            DateTime lastUpdated = DateTime.Now;
            StatusTextBlock.Text = "Last Updated: " + lastUpdated.ToLongTimeString();

            AppSettings.LastUpdated = lastUpdated.ToString();
            AppSettings.Save();

            UpdateExistingTweets();

            TweetCollection addedTweets = new TweetCollection();

            //prevents huge number of notifications appearing on startup
            bool displayPopups = !(tweets.Count == 0);

            // Add the new tweets
            for (int i = newTweets.Count - 1; i >= 0; i--)
            {
                Tweet tweet = newTweets[i];
                if (!tweets.Contains(tweet))
                {
                    tweets.Insert(0, tweet);
                    tweet.Index = tweets.Count;
                    tweet.IsNew = true;
                    addedTweets.Add(tweet);
                }
            }

            // tweets listbox ScrollViewer.CanContentScroll is set to "False", which means it scrolls more smooth,
            // However it disables Virtualization
            // Remove tweets pass 100 should improve performance reasons.
            if(AppSettings.KeepLatest != 0)
                tweets.TruncateAfter(AppSettings.KeepLatest);

            if (addedTweets.Count > 0)
            {
                if (AppSettings.DisplayNotifications && !(bool)this.IsActive)
                    NotifyOnNewTweets(addedTweets);                

                if (AppSettings.PlaySounds)
                {
                    // Author: Keith Elder
                    // I wrapped a try catch around this and added logging.
                    // I found that the Popup screen and this were causing 
                    // a threading issue.  At least that is my theory.  When
                    // new items would come in, and play a sound as well as 
                    // pop a new message there was no need to recreate and load
                    // the wave file.  InitializeSoundPlayer() was added on load
                    // to do that just once.
                    try
                    {
                        // Play tweets found sound for new tweets
                        _player.Play();
                    }
                    catch (Exception ex)
                    {
                        App.Logger.Error("Error playing sound", ex);
                    }
                }
            }

            StopStoryboard("Fetching");


        }

        private void NotifyOnNewTweets(TweetCollection newTweets)
        {
            if (SnarlInterface.SnarlIsActive())
            {
                SnarlNotify(newTweets);
            }
            else
            {
                PopUpNotify(newTweets);
            }
        }

        private void PopUpNotify(TweetCollection newTweets)
        {
            if (newTweets.Count > Double.Parse(AppSettings.MaximumIndividualAlerts))
            {
                Popup p = new Popup("New Tweets", BuiltNewTweetMessage(newTweets), twitter.CurrentlyLoggedInUser.ImageUrl, 0);
                p.FadeOutFinished += new FadeOutFinishedDelegate(RemovePopup);
                p.ReplyClicked += new PopupReplyClickedDelegate(PopupReplyClicked);
                p.DirectMessageClicked += new PopupDirectMessageClickedDelegate(PopupDirectMessageClicked);
                p.Clicked += new PopupClickedDelegate(PopupClicked);
                p.CloseButtonClicked += new PopupCloseButtonClickedDelegate(RemovePopup);
                p.Show();
            }
            else
            {
                int index = 0;
                foreach (Tweet tweet in newTweets)
                {
                    Popup p = new Popup(tweet, index++);
                    p.FadeOutFinished += new FadeOutFinishedDelegate(RemovePopup);
                    p.ReplyClicked += new PopupReplyClickedDelegate(PopupReplyClicked);
                    p.DirectMessageClicked += new PopupDirectMessageClickedDelegate(PopupDirectMessageClicked);
                    p.Clicked += new PopupClickedDelegate(PopupClicked);
                    p.CloseButtonClicked += new PopupCloseButtonClickedDelegate(RemovePopup);
                    p.Show();
                }
            }
        }

        private static string BuiltNewTweetMessage(TweetCollection newTweets)
        {
            string message = string.Format("You have {0} new tweets!\n", newTweets.Count);
            foreach (Tweet tweet in newTweets)
            {
                message += " " + tweet.User.ScreenName;
            }
            if (message.Length > 140)
            {
                message = message.Substring(0, 135);
                int lastSpace = message.LastIndexOf(' ');
                message = message.Substring(0, lastSpace) + "...";
            }
            return TruncateMessage(message);
        }

        private static string TruncateMessage(string message)
        {
            if (message.Length > 140)
            {
                message = message.Substring(0, 135);
                int lastSpace = message.LastIndexOf(' ');
                message = message.Substring(0, lastSpace) + "...";
            }
            return message;
        }

        private void SnarlNotify(TweetCollection newTweets)
        {
            if (newTweets.Count > Double.Parse(AppSettings.MaximumIndividualAlerts))
            {
                SnarlInterface.SendMessage("New Tweets", BuiltNewTweetMessage(newTweets), "", 4);
            }
            else
            {
                foreach (Tweet tweet in newTweets)
                {
                    SnarlInterface.SendMessage(string.Format("New Tweet from {0}", tweet.User.ScreenName), string.Format("{0}\n\n{1}", tweet.Text, tweet.RelativeTime), "", 4);
                }
            }
        }

         private void UpdateExistingTweets()
         {
             UpdateExistingTweets(tweets);
         }

         private static void UpdateExistingTweets(TweetCollection oldTweets)
         {
             // Update existing tweets
             foreach (Tweet tweet in oldTweets)
             {
                 tweet.IsNew = false;
                 tweet.UpdateRelativeTime();
             }
         }

        #endregion

        #region Add new tweet update

        private void UpdateButton_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(TweetTextBox.Text))
            {
                // Schedule posting the tweet

                UpdateButton.Dispatcher.BeginInvoke(
                    DispatcherPriority.Normal,
                    new OneStringArgDelegate(AddTweet), TweetTextBox.Text);
            }
        }

        private void AddTweet(string tweetText)
        {
            try
            {
                //bmsullivan If tweet is short enough, leave real URLs for clarity
                if (tweetText.Length > 140)
                {
                    //parse the text here and tiny up any URLs found.
                    TinyUrlHelper tinyUrls = new TinyUrlHelper();
                    tweetText = tinyUrls.ConvertUrlsToTinyUrls(tweetText);
                }
                Tweet tweet = twitter.AddTweet(tweetText); ;

                // Schedule the update function in the UI thread.
                LayoutRoot.Dispatcher.BeginInvoke(
                DispatcherPriority.Normal,
                new AddTweetUpdateDelegate(UpdatePostUserInterface), tweet);
            }
            catch (WebException ex)
            {
                UpdateTextBlock.Text = "Update failed.";
                App.Logger.Debug(String.Format("There was a problem fetching new tweets from Twitter.com: {0}", ex.ToString()));
            }
        }

        private void UpdatePostUserInterface(Tweet newlyAdded)
        {
            if (newlyAdded != null)
            {
                UpdateTextBlock.Text = "Update";
                StatusTextBlock.Text = "Status Updated!";
                PlayStoryboard("CollapseUpdate");
                isExpanded = false;
                TweetTextBox.Clear();
                tweets.Insert(0, newlyAdded);
            }
            else
            {
                App.Logger.Error("There was a problem posting your tweet to Twitter.com.");
                MessageBox.Show("There was a problem posting your tweet to Twitter.com.");
            }
        }

        private void Update_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            ToggleUpdate();
        }

        private void ToggleUpdate()
        {
            if (isLoggedIn)
            {
                if (!isExpanded)
                {
                    PlayStoryboard("ExpandUpdate");
                    TweetTextBox.Focus();
                    isExpanded = true;
                }
                else
                {
                    PlayStoryboard("CollapseUpdate");
                    isExpanded = false;
                }
            }
        }
        #endregion

        #region Replies

        private void DelegateRepliesFetch()
        {
            // Let the user know what's going on
            StatusTextBlock.Text = "Retrieving replies...";

            PlayStoryboard("Fetching");

            // Create a Dispatcher to fetching new tweets
            NoArgDelegate fetcher = new NoArgDelegate(
                this.GetReplies);

            fetcher.BeginInvoke(null, null);
        }

        private void GetReplies()
        {
            try
            {
                // Schedule the update function in the UI thread.
                LayoutRoot.Dispatcher.BeginInvoke(
                    DispatcherPriority.Normal,
                    new OneArgDelegate(UpdateRepliesInterface), twitter.GetReplies());
            }
            catch (WebException ex)
            {
                App.Logger.Debug(String.Format("There was a problem fetching your replies from Twitter.com. ", ex.Message));
            }
        }

        private void UpdateRepliesInterface(TweetCollection newReplies)
        {
            repliesLastUpdated = DateTime.Now;
            StatusTextBlock.Text = "Replies Updated: " + repliesLastUpdated.ToLongTimeString();

            UpdateExistingTweets(replies);

            for (int i = newReplies.Count - 1; i >= 0; i--)
            {
                Tweet reply = newReplies[i];
                if (!replies.Contains(reply))
                {
                    replies.Insert(0, reply);
                    reply.Index = replies.Count;
                    reply.IsNew = true;
                }
            }

            StopStoryboard("Fetching");
        }

        #endregion

        #region Messages

        private void DelegateMessagesFetch()
        {
            // Let the user know what's going on
            StatusTextBlock.Text = "Retrieving direct messages...";

            PlayStoryboard("Fetching");

            // Create a Dispatcher to fetching new tweets
            NoArgDelegate fetcher = new NoArgDelegate(
                this.GetMessages);

            fetcher.BeginInvoke(null, null);
        }

        private void GetMessages()
        {
            try
            {
                // Schedule the update function in the UI thread.
                LayoutRoot.Dispatcher.BeginInvoke(
                    DispatcherPriority.Normal,
                    new MessagesDelegate(UpdateMessagesInterface), twitter.RetrieveMessages());
            }
            catch (WebException ex)
            {
                App.Logger.Debug(String.Format("There was a problem fetching your direct messages from Twitter.com: {0}", ex.ToString()));
            }
        }

        private void UpdateMessagesInterface(DirectMessageCollection newMessages)
        {
            messagesLastUpdated = DateTime.Now;
            StatusTextBlock.Text = "Messages Updated: " + messagesLastUpdated.ToLongTimeString();

            for (int i = newMessages.Count - 1; i >= 0; i--)
            {
                DirectMessage message = newMessages[i];
                if (!messages.Contains(message))
                {
                    messages.Insert(0, message);
                    message.IsNew = true;
                }
                else
                {
                    // update the relativetime for existing messages
                    //messages[i].UpdateRelativeTime();
                }
            }

            StopStoryboard("Fetching");
        }

        #endregion

        #region Send messages

        private void SendMessageButton_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(MessageUserTextBox.Text) && !string.IsNullOrEmpty(MessageTextBox.Text))
            {
                // Schedule posting the tweet
                UpdateButton.Dispatcher.BeginInvoke(
                    DispatcherPriority.Normal,
                    new SendMessageDelegate(SendMessage), MessageUserTextBox.Text, MessageTextBox.Text);
            }
        }

        private void SendMessage(string user, string messageText)
        {
            try
            {
                twitter.SendMessage(user, messageText);

                // Schedule the update function in the UI thread.
                LayoutRoot.Dispatcher.BeginInvoke(
                    DispatcherPriority.Normal,
                    new NoArgDelegate(UpdateMessageUserInterface));
            }
            catch (WebException ex)
            {
                UpdateTextBlock.Text = "Message failed.";
                App.Logger.Debug(String.Format("There was a problem sending your message: {0}", ex.ToString()));
            }
        }

        private void UpdateMessageUserInterface()
        {
            UpdateTextBlock.Text = "Send Message";
            StatusTextBlock.Text = "Message Sent!";
            PlayStoryboard("CollapseMessage");
            isMessageExpanded = false;
            MessageTextBox.Clear();

            UpdateExistingTweets();
        }

        private void Message_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            ToggleMessage();
        }

        private void ToggleMessage()
        {
            if (isLoggedIn)
            {
                if (!isMessageExpanded)
                {
                    PlayStoryboard("ExpandMessage");
                    MessageTextBox.Focus();
                    isMessageExpanded = true;
                }
                else
                {
                    PlayStoryboard("CollapseMessage");
                    isMessageExpanded = false;
                }
            }
        }

        #endregion

        #region User Timline

        private void DelegateUserTimelineFetch(string userId)
        {
            displayUser = userId;

            UserTab.IsSelected = true;
            userTweets.Clear();

            // Let the user know what's going on
            StatusTextBlock.Text = "Retrieving user's tweets...";

            PlayStoryboard("Fetching");

            // Create a Dispatcher to fetching new tweets
            LayoutRoot.Dispatcher.BeginInvoke(
                DispatcherPriority.Normal,
                new OneStringArgDelegate(GetUserTimeline), userId);
        }

        private void GetUserTimeline(string userId)
        {
            try
            {
                // Schedule the update function in the UI thread.
                LayoutRoot.Dispatcher.BeginInvoke(
                    DispatcherPriority.Normal,
                    new OneArgDelegate(UpdateUsersTimelineInterface), twitter.GetUserTimeline(userId));
            }
            catch (WebException ex)
            {
                App.Logger.Debug(String.Format("There was a problem fetching the user's timeline from Twitter.com: {0}", ex.ToString()));
            }
        }

        private void UpdateUsersTimelineInterface(TweetCollection newTweets)
        {
            StatusTextBlock.Text = displayUser + "'s Timeline Updated: " + repliesLastUpdated.ToLongTimeString();

            for (int i = newTweets.Count - 1; i >= 0; i--)
            {
                Tweet tweet = newTweets[i];
                if (!userTweets.Contains(tweet))
                {
                    userTweets.Insert(0, tweet);
                    tweet.IsNew = true;
                }
                else
                {
                    // update the relativetime for existing tweets
                    userTweets[i].UpdateRelativeTime();
                }
            }

            if (userTweets.Count > 0)
                UserTimelineListBox.SelectedIndex = 0;

            StopStoryboard("Fetching");
        }

        #endregion

        #region Login

        private void TryLogin()
        {
            try
            {
                // Schedule the update function in the UI thread.
                LayoutRoot.Dispatcher.BeginInvoke(
                    DispatcherPriority.Normal,
                    new LoginDelegate(UpdatePostLoginInterface), twitter.Login());
            }
            catch (WebException ex)
            {
                App.Logger.Debug(String.Format("There was a problem logging in to Twitter: {0}", ex.ToString()));
            }
            catch (ProxyAuthenticationRequiredException ex)
            {
                App.Logger.Error("Incorrect proxy configuration.");
                MessageBox.Show("Proxy server is configured incorrectly.  Please correct the settings on the Options menu.");
                LayoutRoot.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new NoArgDelegate(UpdateLoginFailedInterface));
            }
        }

        private void UpdatePostLoginInterface(User user)
        {
            App.LoggedInUser = user;
            if (App.LoggedInUser != null)
            {
                isLoggedIn = true;
                RefreshButton.IsEnabled = true;
                OptionsButton.IsEnabled = true;
                FilterToggleButton.IsEnabled = true;
                AppSettings.LastUpdated = string.Empty;
                Filter.IsEnabled = true;

                DelegateRecentFetch();

                // Setup refresh timer
                refreshTimer.Interval = refreshInterval;
                refreshTimer.Tick += new EventHandler(Timer_Elapsed);
                refreshTimer.Start();
            }
            else
            {
                // login info from user settings is not valid, re-display the login screen.
                PlayStoryboard("ShowLogin");
            }
        }

        private void UpdateLoginFailedInterface()
        {
            isLoggedIn = false;
            OptionsButton.IsEnabled = true;
        }

        private void LoginControl_Login(object sender, RoutedEventArgs e)
        {
            twitter = new TwitterNet(AppSettings.Username, TwitterNet.DecryptString(AppSettings.Password), WebProxyHelper.GetConfiguredWebProxy());

            // fetch new tweets
            DelegateRecentFetch();

            // Setup refresh timer to get subsequent tweets
            refreshTimer.Interval = refreshInterval;
            refreshTimer.Tick += new EventHandler(Timer_Elapsed);
            refreshTimer.Start();

            PlayStoryboard("HideLogin");

            isExpanded = false;
            isLoggedIn = true;
            OptionsButton.IsEnabled = true;
            FilterToggleButton.IsEnabled = true;
            Filter.IsEnabled = true;
        }

        #endregion

        #region Misc Methods and Event Handlers

        /// <summary>
        /// This event is VERY important since it traps errors that happen unexpectedly.  Witty has been unstable
        /// due to the fact that there are actions in the API that don't account for the business rules.  So when 
        /// an action occurs, witty crashes.  This handler traps those errors and logs them.  
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        void Dispatcher_UnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            //TODO: Figure out a better option to do with these unhandled exceptions.  Maybe email them or something?
            App.Logger.Error("Unhandled exception occurred.", e.Exception);
#if DEBUG
            string error = String.Empty;
            if (e.Exception.InnerException != null)
            {
                error = e.Exception.InnerException.Message;
            }
            else
            {
                error = e.Exception.Message;
            }
            MessageBox.Show("An unhandled error occurred. See the log for details.\nError: " + error, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
#endif
            e.Handled = true;
        }

        private void InitializeClickOnceTimer()
        {
            if (ApplicationDeployment.IsNetworkDeployed)
            {
                // Initialize clickonce deployment
                _clickOnce = new Deployment(StatusTextBlock);
                _clickOnce.UpdateStartedEvent += new Deployment.UpdateStartedDelegate(clickOnce_UpdateStartedEvent);
                _clickOnce.UpdateCompletedEvent += new Deployment.UpdateCompletedDelegate(clickOnce_UpdateCompletedEvent);

                // initialize timer for click once updates
                _clickOnceUpdateTimer = new DispatcherTimer();
                _clickOnceUpdateTimer.Interval = new TimeSpan(0, 0, AppSettings.ClickOnceUpdateInterval);
                _clickOnceUpdateTimer.IsEnabled = true;
                _clickOnceUpdateTimer.Start();
                _clickOnceUpdateTimer.Tick += new EventHandler(_clickOnceUpdateTimer_Tick);

                // update window with clickonce version number
                this.Title = AppSettings.ApplicationName + " " + ApplicationDeployment.CurrentDeployment.CurrentVersion.ToString();
            }
        }

        private void InitializeSoundPlayer()
        {
            _player = new SoundPlayer(Witty.Properties.Resources.alert);
            _player.LoadAsync();
        }

        void _clickOnceUpdateTimer_Tick(object sender, EventArgs e)
        {
            StatusTextBlock.Text = "Starting update...";
            _clickOnce.UpdateApplication();
        }

        void clickOnce_UpdateCompletedEvent(bool restartApplication)
        {
            // restart the timeer
            _clickOnceUpdateTimer.Start();

            if (restartApplication)
            {
                System.Windows.Forms.Application.Restart();
                Application.Current.Shutdown();
            }
            else
            {
                StatusTextBlock.Text = "Last updated: " + AppSettings.LastUpdated;
            }
        }

        void clickOnce_UpdateStartedEvent()
        {
            StatusTextBlock.Text = "Update started...";
            _clickOnceUpdateTimer.Stop();
        }

        /// <summary>
        /// Checks for keyboard shortcuts
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e">EventArgs</param>
        private void MainWindow_OnKeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (isLoggedIn)
                {
                    switch (e.Key)
                    {
                        case Key.D:
                            createDirectMessage();
                            break;
                        case Key.U:
                            ToggleUpdate();
                            break;
                        case Key.R:
                            createReply();
                            break;
                        case Key.F:
                            createRetweet();
                            break;
                        case Key.O:
                            showOptions();
                            break;
                        case Key.D1:
                            //show the "Recent" tab
                            Tabs.SelectedIndex = 0;
                            break;
                        case Key.D2:
                            //show the "Replies" tab
                            Tabs.SelectedIndex = 1;
                            break;
                        case Key.D3:
                            //show the "Users" tab
                            Tabs.SelectedIndex = 2;
                            break;
                        case Key.D4:
                            //show the "Messages" tab
                            Tabs.SelectedIndex = 3;
                            break;
                    }
                }
                else
                {
                    if (e.Key == Key.Q) { App.Current.Shutdown(); };
                }
            }
            else
            {
                if (e.Key == Key.F5) { this.Refresh(); };

                if (e.Key == Key.Escape) { this.WindowState = WindowState.Minimized; };
            }
        }

        private void createDirectMessage()
        {
            Tweet selectedTweet = SelectedTweet as Tweet;
            if (null != selectedTweet)
            {
                createDirectMessage(selectedTweet.User.ScreenName);
            }
        }

        private void createDirectMessage(string screenName)
        {
            //Direct message to user
            if (!isExpanded)
            {
                this.Tabs.SelectedIndex = 0;
                ToggleUpdate();
            }
            TweetTextBox.Text = "";
            TweetTextBox.Text = "D ";

            TweetTextBox.Text += screenName;
            TweetTextBox.Select(TweetTextBox.Text.Length, 0);
        }

        private void createReply()
        {
            //reply to user
            if (null != SelectedTweet)
            {
                createReply(SelectedTweet.User.ScreenName);
            }
        }

        private void createReply(string screenName)
        {
            if (!isExpanded)
            {
                this.Tabs.SelectedIndex = 0;
                ToggleUpdate();
            }
            TweetTextBox.Text = "";
            TweetTextBox.Text = "@" + screenName + " ";
            TweetTextBox.Select(TweetTextBox.Text.Length, 0);
        }

        private void deleteTweet()
        {
            if (null != SelectedTweet)
            {
                deleteTweet(SelectedTweet.Id);
            }
        }

        private void deleteTweet(double id)
        {

            /* By: Keith Elder
             * You can only destroy a tweet if you are the one that created it
             * or if it is a direct message to you.  This is causing exceptions.
             */
            if (SelectedTweet.User.ScreenName == Settings.Default.Username)
            {
                if (MessageBoxResult.Yes == MessageBox.Show("Are you sure you want to permanently delete your tweet?\nThis action is irreversible. Select No to only delete it from the application or Yes to delete permanently.", Settings.Default.ApplicationName, MessageBoxButton.YesNo, MessageBoxImage.Question))
                {
                    LayoutRoot.Dispatcher.BeginInvoke(
                                    DispatcherPriority.Normal,
                                    new DeleteTweetDelegate(twitter.DestroyTweet), id);
                }
                if (tweets.Contains(SelectedTweet))
                {
                    tweets.Remove(SelectedTweet);
                }
                else if (replies.Contains(SelectedTweet))
                {
                    replies.Remove(SelectedTweet);
                }
            }
        }

        private void FollowUser()
        {
            if (null != SelectedTweet)
            {
                FollowUser(SelectedTweet.User.ScreenName);
            }
        }

        private void FollowUser(string username)
        {
            LayoutRoot.Dispatcher.BeginInvoke(DispatcherPriority.Normal,
                new OneStringArgDelegate(twitter.FollowUser), username);
        }

        private void deleteDirectMessage()
        {
            DirectMessage message = MessagesListBox.SelectedItem as DirectMessage;
            if (message != null)
            {
                deleteDirectMessage(message.Id);
                if (messages.Contains(message))
                {
                    messages.Remove(message);
                }
            }
        }

        private void deleteDirectMessage(double id)
        {
            twitter.DestroyDirectMessage(id);
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            this.Refresh();
        }

        private void Refresh()
        {
            switch (currentView)
            {
                case CurrentView.Recent:
                    DelegateRecentFetch();
                    break;
                case CurrentView.Replies:
                    DelegateRepliesFetch();
                    break;
                case CurrentView.Messages:
                    DelegateMessagesFetch();
                    break;
                case CurrentView.User:
                    DelegateUserTimelineFetch(displayUser);
                    break;
            }
        }

        #region Clear Methods

        internal void ClearTweets()
        {
            tweets.Clear();
        }

        internal void ClearReplies()
        {
            replies.Clear();
        }

        private void Clear()
        {
            switch (currentView)
            {
                case CurrentView.Recent:
                    ClearTweets();
                    break;
                case CurrentView.Replies:
                    ClearReplies();
                    break;
            }
        }
        #endregion

        private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            TabControl tabs = (TabControl)sender;

            if (tabs.SelectedIndex == 0)
            {
                displayUser = string.Empty;
            }

            if (tabs.SelectedIndex == 1 && isLoggedIn)
            {
                // limit updating replies to no more than once a minute
                long ticks = DateTime.Now.Ticks - repliesLastUpdated.Ticks;
                TimeSpan ts = new TimeSpan(ticks);
                if (ts.TotalMinutes > 1)
                {
                    DelegateRepliesFetch();
                }

                displayUser = string.Empty;
            }

            if (tabs.SelectedIndex == 2 && string.IsNullOrEmpty(displayUser))
            {
                DelegateUserTimelineFetch(AppSettings.Username);
            }

            if (tabs.SelectedIndex == 3 && isLoggedIn)
            {
                // limit updating replies to no more than once a minute
                long ticks = DateTime.Now.Ticks - messagesLastUpdated.Ticks;
                TimeSpan ts = new TimeSpan(ticks);
                if (ts.TotalMinutes > 1)
                {
                    DelegateMessagesFetch();
                }

                displayUser = string.Empty;
            }

            // clear the filter text since it isn't applied when switching tabs
            FilterTextBox.Text = string.Empty;
        }

        private void PlayStoryboard(string storyboardName)
        {
            Object o = TryFindResource(storyboardName);
            if (o != null)
            {
                Storyboard storyboard = (Storyboard)o;
                storyboard.Begin(this);
            }
        }

        private void StopStoryboard(string storyboardName)
        {
            Object o = TryFindResource(storyboardName);
            if (o != null)
            {
                Storyboard storyboard = (Storyboard)o;
                storyboard.Stop(this);
            }
        }

        private void TweetsListBox_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (e.MouseDevice.DirectlyOver != null && e.MouseDevice.DirectlyOver.GetType() == typeof(TextBlock))
            {
                TextBlock textBlock = (TextBlock)e.MouseDevice.DirectlyOver;

                try
                {
                    ListBox listbox = (ListBox)sender;

                    if (textBlock.Name == "ScreenName")
                    {
                        if (listbox.SelectedItem != null && currentView != CurrentView.User)
                        {
                            Tweet tweet = (Tweet)listbox.SelectedItem;
                            //System.Diagnostics.Process.Start(tweet.User.TwitterUrl);
                            DelegateUserTimelineFetch(tweet.User.ScreenName);
                        }
                    }
                }
                catch (Win32Exception ex)
                {
                    App.Logger.Debug(String.Format("Exception: {0}", ex.ToString()));
                }
            }
        }

        private void MessagesListBox_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (e.MouseDevice.DirectlyOver.GetType() == typeof(TextBlock))
            {
                TextBlock textBlock = (TextBlock)e.MouseDevice.DirectlyOver;

                try
                {
                    ListBox listbox = (ListBox)sender;

                    if (textBlock.Name == "ScreenName")
                    {
                        if (listbox.SelectedItem != null && currentView != CurrentView.User)
                        {
                            DirectMessage tweet = (DirectMessage)listbox.SelectedItem;

                            ToggleMessage();
                            MessageUserTextBox.Text = textBlock.Text;
                            MessageTextBox.Focus();
                        }
                    }
                }
                catch (Win32Exception ex)
                {
                    App.Logger.Debug(String.Format("Exception: {0}", ex.ToString()));
                }
            }
        }

        void AlwaysOnTop_Click(object sender, RoutedEventArgs e)
        {
            if (((MenuItem)sender).IsChecked)
                this.Topmost = true;
            else
                this.Topmost = false;

            AppSettings.AlwaysOnTop = this.Topmost;
            AppSettings.Save();
        }

        private void Url_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            TextBlock textBlock = (TextBlock)sender;
            try
            {
                System.Diagnostics.Process.Start(textBlock.Text);
            }
            catch (Win32Exception ex)
            {
                App.Logger.Debug(String.Format("Exception: {0}", ex.ToString()));
            }
        }

        private void ScreenName_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            TextBlock textBlock = (TextBlock)sender;
            try
            {
                System.Diagnostics.Process.Start(textBlock.Tag.ToString());
            }
            catch (Win32Exception ex)
            {
                App.Logger.Debug(String.Format("Exception: {0}", ex.ToString()));
            }
        }

        private void OptionsButton_Click(object sender, RoutedEventArgs e)
        {
            showOptions();
        }

        private void showOptions()
        {
            Options options = new Options();

            Binding binding = new Binding();
            binding.Path = new PropertyPath("Topmost");
            binding.Source = this;
            binding.Mode = BindingMode.TwoWay;
            options.SetBinding(Options.TopmostProperty, binding);
            options.Owner = this;

            bool? dialogResult = options.ShowDialog();
            switch (dialogResult)
            {
                case true:
                    // User wants to save options

                    // update the refresh interval
                    int minutes = int.Parse(AppSettings.RefreshInterval);

                    refreshTimer.Stop();
                    if (minutes > 0)
                    {
                        refreshInterval = new TimeSpan(0, minutes, 0);
                        refreshTimer.Interval = refreshInterval;
                        refreshTimer.Start();
                    }

                    StatusTextBlock.Text = "Options Updated";

                    break;
                case false:
                    break;
                default:
                    // Indeterminate, do nothing
                    break;
            }
            if (string.IsNullOrEmpty(AppSettings.Username))
            {
                // User wants to logout
                isLoggedIn = false;
                tweets.Clear();
                StatusTextBlock.Text = "Login";
                Filter.IsEnabled = false;

                PlayStoryboard("ShowLogin");
            }
        }

        #region Context menu event handlers

        private void ContextMenuReply_Click(object sender, RoutedEventArgs e)
        {
            createReply();
        }

        private void ContextMenuRetweet_Click(object sender, RoutedEventArgs e)
        {
            createRetweet();
        }

        private void createRetweet()
        {
            Tweet selectedTweet = SelectedTweet as Tweet;
            if (selectedTweet != null)
            {
                if (!isExpanded)
                {
                    this.Tabs.SelectedIndex = 0;
                    ToggleUpdate();
                }
                string message = string.Format("retweet @{0}: {1}", selectedTweet.User.ScreenName, selectedTweet.Text);
                message = TruncateMessage(message);
                TweetTextBox.Text = message;
                TweetTextBox.Select(TweetTextBox.Text.Length, 0);
            }

        }

        private void ContextMenuDeleteMessage_Click(object sender, RoutedEventArgs e)
        {
            deleteDirectMessage();
        }


        private void ContextMenuDirectMessage_Click(object sender, RoutedEventArgs e)
        {
            createDirectMessage();
        }

        private void ContextMenuFollow_Click(object sender, RoutedEventArgs e)
        {
            FollowUser();
        }

        private void ContextMenuDelete_Click(object sender, RoutedEventArgs e)
        {
            deleteTweet();
        }

        private void ContextMenuClear_Click(object sender, RoutedEventArgs e)
        {
            Clear();
        }

        #endregion

        #region Popup Event Handlers

        private void RemovePopup(Popup popup)
        {
            popupCount--;
            popup.Close();
            popup = null;
        }

        private void PopupReplyClicked(string screenName)
        {
            if (this.WindowState == WindowState.Minimized)
            {
                Show();
                WindowState = _storedWindowState;
            }
            createReply(screenName);
        }

        private void PopupDirectMessageClicked(string screenName)
        {
            if (this.WindowState == WindowState.Minimized)
            {
                Show();
                WindowState = _storedWindowState;
            }
            createDirectMessage(screenName);
        }

        void PopupClicked(Tweet tweet)
        {
            if (this.WindowState == WindowState.Minimized)
            {
                Show();
                WindowState = _storedWindowState;
            }

            if (tweet != null)
            {
                TweetsListBox.ScrollIntoView(tweet);
            }
        }

        #endregion

        #endregion

        #region Filter

        // Delegate for performing filter in background thread for performance improvements
        private delegate void FilterDelegate();

        /// <summary>
        /// Handles the filtering
        /// </summary>
        private void FilterTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // Start an async operation that filters the list.
            this.Dispatcher.BeginInvoke(
                DispatcherPriority.ApplicationIdle,
                new FilterDelegate(FilterWorker));
        }

        /// <summary>
        /// Worker method that filters the list.
        /// </summary>
        private void FilterWorker()
        {
            //Use collection view to filter the listbox
            ICollectionView collectionView = CollectionViewSource.GetDefaultView(tweets);

            switch (currentView)
            {
                case CurrentView.Recent:
                    collectionView = CollectionViewSource.GetDefaultView(tweets);
                    break;
                case CurrentView.Replies:
                    collectionView = CollectionViewSource.GetDefaultView(replies);
                    break;
                case CurrentView.Messages:
                    collectionView = CollectionViewSource.GetDefaultView(messages);
                    break;
                case CurrentView.User:
                    collectionView = CollectionViewSource.GetDefaultView(userTweets);
                    break;
                default:
                    collectionView = CollectionViewSource.GetDefaultView(tweets);
                    break;
            }

            if (currentView == CurrentView.Messages)
                // messages aren't tweets
                collectionView.Filter = new Predicate<object>(MessageFilter);
            else
                collectionView.Filter = new Predicate<object>(TweetFilter);
        }

        /// <summary>
        /// Delegate to filter the tweet text and by the tweet user's screenname.
        /// </summary>
        /// <param name="item"></param>
        /// <returns></returns>
        public bool TweetFilter(object item)
        {
            Tweet tweet = item as Tweet;

            // this will prevent the fade animation from starting when the tweet is filtered
            tweet.IsNew = false;

            return (tweet.Text.ToLower().Contains(FilterTextBox.Text.ToLower()))
                   || (tweet.User.ScreenName.ToLower().Contains(FilterTextBox.Text.ToLower()));
        }

        /// <summary>
        /// Delegate to filter the tweet text and by the tweet user's screenname.
        /// </summary>
        public bool MessageFilter(object item)
        {
            DirectMessage message = item as DirectMessage;
            return (message.Text.ToLower().Contains(FilterTextBox.Text.ToLower()))
                   || (message.Sender.ScreenName.ToLower().Contains(FilterTextBox.Text.ToLower()));
        }

        #endregion

        #region Minimize to Tray

        private System.Windows.Forms.NotifyIcon _notifyIcon;

        void OnClosed(object sender, EventArgs e)
        {
            if (!AppSettings.PersistLogin)
            {
                AppSettings.Username = string.Empty;
                AppSettings.Password = string.Empty;
                AppSettings.Save();
            }

            _notifyIcon.Dispose();
            _notifyIcon = null;

            if (SnarlInterface.SnarlIsActive() && this.SnarlConfighWnd != null)
            {
                SnarlInterface.RevokeConfig(this.SnarlConfighWnd.ToInt32());
                Win32.DestroyWindow(this.SnarlConfighWnd);
            }
        }

        private WindowState _storedWindowState = WindowState.Normal;

        DispatcherTimer hideTimer = new DispatcherTimer();

        void OnStateChanged(object sender, EventArgs args)
        {
            if (AppSettings.MinimizeToTray)
            {
                if (WindowState == WindowState.Minimized)
                {
                    hideTimer.Interval = new TimeSpan(500);
                    hideTimer.Tick += new EventHandler(HideTimer_Elapsed);
                    hideTimer.Start();
                }
                else
                {
                    _storedWindowState = WindowState;
                }
            }
        }

        private void HideTimer_Elapsed(object sender, EventArgs e)
        {
            this.Hide();
            hideTimer.Stop();
        }

        void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs args)
        {
            CheckTrayIcon();
        }

        void m_notifyIcon_Click(object sender, EventArgs e)
        {
            Show();
            WindowState = _storedWindowState;
        }

        void CheckTrayIcon()
        {
            ShowTrayIcon(!IsVisible);
        }

        void ShowTrayIcon(bool show)
        {
            if (_notifyIcon != null)
                _notifyIcon.Visible = show;
        }

        void openMenuItem_Click(object sender, EventArgs e)
        {
            Show();
            WindowState = _storedWindowState;
        }

        void exitMenuItem_Click(object sender, EventArgs e)
        {
            this.reallyexit = true;
            this.Close();
        }

        #endregion

        #region Single Instance
        SingleInstanceManager _instanceManager;

        public void ShowApplication()
        {
            if (this.Visibility == Visibility.Hidden)
            {
                this.Visibility = Visibility.Visible;
            }
        }

        void Current_Exit(object sender, ExitEventArgs e)
        {
            Environment.Exit(0);
        }

        #endregion

        #region Filter and Search

        private void TweetScanButton_Click(object sender, RoutedEventArgs e)
        {
            TweetScanHelper ts = new TweetScanHelper();
            TweetCollection searchResults = ts.GetSearchResults(FilterTextBox.Text);

            // TODO: this should be displayed somewhere else instead of the main tweets listbox.
            for (int i = searchResults.Count - 1; i >= 0; i--)
            {
                Tweet tweet = searchResults[i];
                if (!tweets.Contains(tweet))
                {
                    tweets.Insert(0, tweet);
                    tweet.Index = tweets.Count;
                    tweet.IsNew = true;
                    tweet.IsSearchResult = true;
                }
            }
        }

        private void FilterToggleButton_Unchecked(object sender, RoutedEventArgs e)
        {
            //TODO: fix this
            //TODO: need to remove the search tweets from the main list.
            //foreach (Tweet t in tweets)
            //{
            //    if (t.IsSearchResult)
            //    {
            //        tweets.Remove(t);
            //    }
            //}
        }

        #endregion

    }
}