#if DEBUG
using Lumi.Models;
using Lumi.Services;

namespace Lumi;

internal static class InteractiveControlsFixture
{
    public static List<Chat> Create(DataStore store)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LUMI_APPDATA_DIR")))
            throw new InvalidOperationException("The interactive UI fixture requires isolated app data.");
        var existing = store.Data.Chats.Where(chat => chat.Title.StartsWith("UI Lab - ", StringComparison.Ordinal)).ToList();
        foreach (var chat in existing)
            store.LoadChatMessagesAsync(chat).GetAwaiter().GetResult();

        var examples = new (string Name, string Kind, string Json)[]
        {
            ("Forms", "form", """
                {"id":"trip_details","title":"Make the weekend yours","description":"A few preferences, one submission. Lumi takes it from here.","submitLabel":"Find matching options","fields":[{"id":"destination","label":"Destination","value":"Kyoto","required":true,"placeholder":"Where would you like to go?"},{"id":"departure","label":"Departure","type":"date","value":"2026-10-17","required":true},{"id":"guests","label":"Guests","type":"number","value":"2","minimum":1,"maximum":20},{"id":"budget","label":"Budget","type":"number","value":"1200","minimum":0,"unit":"USD","required":true},{"id":"pace","label":"Your pace","type":"select","value":"Balanced","options":["Relaxed","Balanced","Packed"]},{"id":"interests","label":"What matters most?","type":"multiselect","options":["Food","Nature","Culture"]}]}
                """),
            ("Checklists", "checklist", """
                {"id":"dinner_list","title":"Ready for a relaxed dinner","description":"Your shopping and prep, kept with this conversation.","items":[{"id":"tomatoes","label":"Cherry tomatoes","meta":"500 g"},{"id":"basil","label":"Fresh basil","meta":"1 bunch"},{"id":"pasta","label":"Pasta and mozzarella","detail":"Enough for four guests."},{"id":"table","label":"Set the table","detail":"A small step that makes the evening feel ready."}]}
                """),
            ("Steps", "checklist", """
                {"id":"router_setup","title":"Set up your new router","numbered":true,"items":[{"id":"connect","label":"Connect the router to your modem","detail":"Use the cable in the box and the port marked WAN."},{"id":"power","label":"Power on and wait for a solid light","detail":"Usually under two minutes.","meta":"2 min"},{"id":"app","label":"Open the setup app","detail":"Join the network printed on the label."},{"id":"password","label":"Set a strong Wi-Fi password"}]}
                """),
            ("Results", "results", """
                {"id":"stay_options","title":"Choose the stay that fits","description":"Synthetic sample options for UI testing, not live prices or availability.","items":[{"id":"garden","label":"A quiet garden stay","meta":"Sample: USD 180 / night · Relaxed","detail":"A peaceful base with green space and room to unwind."},{"id":"city","label":"A walkable city hideaway","meta":"Sample: USD 220 / night · Central","detail":"Stay close to food, culture, and the places you want to explore."},{"id":"retreat","label":"A design-led retreat","meta":"Sample: USD 260 / night · Spacious","detail":"A little more room and a slower pace for a special weekend."}]}
                """),
            ("Calculators", "calculator", """
                {"id":"savings_plan","title":"See your savings grow","description":"Change an assumption and explore the difference instantly.","template":"savings","currency":"USD","inputs":{"initial":5000,"monthly":250,"annualRate":5,"years":10}}
                """),
            ("Loans", "calculator", """
                {"id":"mortgage","title":"Estimate a mortgage","description":"Synthetic numbers for UI testing.","template":"loan","currency":"USD","inputs":{"loanAmount":350000,"interestRate":6.2,"years":30}}
                """),
            ("Budgets", "calculator", """
                {"id":"monthly_budget","title":"Plan this month's budget","template":"budget","currency":"USD","inputs":{"income":5200,"essential":2600,"flexible":1100,"savings":800}}
                """),
            ("Recipes", "calculator", """
                {"id":"pasta_scale","title":"Scale the recipe","template":"servings","inputs":{"baseServings":4,"servings":6},"ingredients":[{"label":"Spaghetti","quantity":400,"unit":"g"},{"label":"Olive oil","quantity":0.25,"unit":"cup"},{"label":"Garlic cloves","quantity":4},{"label":"Chili flakes","quantity":0.5,"unit":"tsp"}]}
                """),
            ("Quizzes", "quiz", """
                {"id":"ui_practice","title":"Check your understanding","description":"Try a quick question. Hints and feedback stay local.","questions":[{"id":"q1","prompt":"Which action sends your choices to Lumi?","options":["Changing a calculator input","Clicking Continue","Checking a shopping item"],"correctIndex":1,"explanation":"Editing is local. Continue is an explicit action that sends your choices as a visible user message.","hint":"Look for the action at the bottom of the control."},{"id":"q2","prompt":"What does a checklist checkmark mean?","options":["Lumi executed and verified that task","You marked that item as done"],"correctIndex":1,"explanation":"These checkmarks belong to your progress. Tool cards and plan status separately describe work performed by Lumi."}]}
                """)
        };
        var chats = new List<Chat>();
        foreach (var example in examples)
        {
            var title = "UI Lab - " + example.Name;
            var previous = existing.FirstOrDefault(chat => chat.Title == title);
            if (previous is { Messages.Count: >= 2 })
            {
                chats.Add(previous);
                continue;
            }
            var chat = previous ?? new Chat
            {
                Title = title,
                LastModelUsed = store.Data.Settings.PreferredModel,
                LastReasoningEffortUsed = store.Data.Settings.ReasoningEffort
            };
            chat.Messages.Add(new ChatMessage
            {
                Role = "user", Content = $"Show me the {example.Name.ToLowerInvariant()} interaction.",
                Author = "You"
            });
            chat.Messages.Add(new ChatMessage
            {
                Role = "assistant",
                Content = $"Try the native **{example.Name.ToLowerInvariant()}** below. This is an isolated UI fixture.\n\n```{example.Kind}\n{example.Json.Trim()}\n```",
                Author = "Lumi"
            });
            chat.MessageCount = chat.Messages.Count;
            if (previous is null)
                store.Data.Chats.Add(chat);
            store.MarkChatChanged(chat);
            store.SaveChatAsync(chat).GetAwaiter().GetResult();
            chats.Add(chat);
        }
        var receiptChat = existing.FirstOrDefault(chat => chat.Title == "UI Lab - Action receipts");
        if (receiptChat is null)
        {
            receiptChat = new Chat { Title = "UI Lab - Action receipts", LastModelUsed = store.Data.Settings.PreferredModel };
            var json = examples.First(example => example.Kind == "results").Json;
            var source = new ChatMessage
            {
                Role = "assistant", Author = "Lumi",
                Content = $"Synthetic UI sample. Pick results locally, then compare them.\n\n```results\n{json.Trim()}\n```"
            };
            var definition = StrataTheme.Controls.InteractiveDefinition.Parse("results", json);
            var state = new StrataTheme.Controls.InteractiveState { SelectedIds = ["garden", "city"] };
            var receipt = InteractiveResponseState.CreateReceipt(source, definition, state, "compare", store.Data.Settings.Language);
            receipt.Status = "ReplyReady";
            source.InteractionStates = new() { [definition.Id] = state.ToJson() };
            receiptChat.Messages.Add(new ChatMessage { Role = "user", Content = "Show how a selection becomes an action receipt.", Author = "You" });
            receiptChat.Messages.Add(source);
            receiptChat.Messages.Add(new ChatMessage
            {
                Role = "user", Author = "You", InteractionReceipt = receipt,
                Content = StrataTheme.Controls.InteractiveSubmission.Build(definition, state, "compare")
            });
            receiptChat.Messages.Add(new ChatMessage
            {
                Role = "assistant", Author = "Lumi",
                Content = "The receipt keeps the exact two selections. **View inputs** reveals the submitted snapshot; **Show original** returns to the source control. Editing the source does not rewrite the receipt."
            });
            store.Data.Chats.Add(receiptChat);
            store.MarkChatChanged(receiptChat);
            store.SaveChatAsync(receiptChat).GetAwaiter().GetResult();
        }
        chats.Add(receiptChat);
        store.Save();
        return chats;
    }
}
#endif
