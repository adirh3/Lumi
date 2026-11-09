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
            ("Explorer stays", "explorer", """
                {"id":"weekend_stays","title":"Places to stay for the weekend","description":"Synthetic sample stays for UI testing, with Wikimedia Commons photos; not live prices or availability.","attributes":[{"id":"price","label":"Per night","type":"money","unit":"USD","better":"lower"},{"id":"rating","label":"Guest rating","type":"rating","better":"higher"},{"id":"area","label":"Area","type":"text"},{"id":"distance","label":"To the center","type":"number","unit":"km","better":"lower"},{"id":"breakfast","label":"Breakfast included","type":"boolean"},{"id":"pool","label":"Pool","type":"boolean"},{"id":"cancel","label":"Free cancellation","type":"boolean"}],"items":[
                  {"id":"harbor","label":"Harbor Light Hotel","subtitle":"Waterfront · 4-star","badge":"Best overall","summary":"Bright rooms over the marina, a short walk from the old town and its cafés.","pros":["Sea-view rooms","Excellent breakfast"],"cons":["Street noise on weekends"],"url":"https://example.com/stays/harbor","image":"https://thumb.wikimedia.org/wikipedia/commons/thumb/b/b7/Hotel_room_interior_at_hotel_Radisson_Blu_Oulu.jpg/500px-Hotel_room_interior_at_hotel_Radisson_Blu_Oulu.jpg","values":{"price":189,"rating":9.1,"area":"Waterfront","distance":1.2,"breakfast":true,"pool":false,"cancel":true}},
                  {"id":"palma","label":"Hotel Casa Palma","subtitle":"Old Town · Historic","badge":"Best value","summary":"A grand old hotel with a palm-lined pool, right in the historic center.","pros":["Pool and garden","Steps from the sights"],"cons":["Rooms show their age","No free cancellation"],"url":"https://example.com/stays/palma","image":"https://thumb.wikimedia.org/wikipedia/commons/thumb/2/21/Havana_-_Swimming_pool%2C_Hotel_Nacional_de_Cuba.jpg/500px-Havana_-_Swimming_pool%2C_Hotel_Nacional_de_Cuba.jpg","values":{"price":156,"rating":8.7,"area":"Old Town","distance":0.4,"breakfast":true,"pool":true,"cancel":false}},
                  {"id":"jasmine","label":"Jasmine Garden Resort","subtitle":"Riverside · Resort & spa","badge":"Splurge","summary":"A quiet resort with a long pool, spa, and gardens by the river.","pros":["Spa and long pool","Very quiet"],"cons":["Taxi needed for the center"],"url":"https://example.com/stays/jasmine","image":"https://thumb.wikimedia.org/wikipedia/commons/thumb/e/ef/Swimming_pool_and_main_building_of_Amantaka_luxury_Resort_%26_Hotel_in_Luang_Prabang_Laos.jpg/500px-Swimming_pool_and_main_building_of_Amantaka_luxury_Resort_%26_Hotel_in_Luang_Prabang_Laos.jpg","values":{"price":342,"rating":9.4,"area":"Riverside","distance":3.5,"breakfast":true,"pool":true,"cancel":true}},
                  {"id":"flores","label":"Las Flores Boutique B&B","subtitle":"Old Town · Boutique","summary":"Eight rooms around a courtyard, run by a family that knows the town.","pros":["Warm hosts","Homemade breakfast"],"cons":["No elevator"],"url":"https://example.com/stays/flores","image":"https://thumb.wikimedia.org/wikipedia/commons/thumb/c/c4/Las_Casas_B%2BB_Boutique_Hotel_exterior_facade_Cuernavaca_Mexico.jpg/500px-Las_Casas_B%2BB_Boutique_Hotel_exterior_facade_Cuernavaca_Mexico.jpg","values":{"price":128,"rating":9.0,"area":"Old Town","distance":0.8,"breakfast":true,"pool":false,"cancel":true}},
                  {"id":"terra","label":"Terra Hillside Hotel","subtitle":"Hillside · Pool and views","summary":"Terraced rooms and an infinity pool looking over the valley.","pros":["Sunset views","Big pool"],"cons":["Steep walk into town"],"url":"https://example.com/stays/terra","image":"https://thumb.wikimedia.org/wikipedia/commons/thumb/b/b8/Swimming_pool_at_Hotel_Terra_Barichara.jpg/500px-Swimming_pool_at_Hotel_Terra_Barichara.jpg","values":{"price":172,"rating":8.4,"area":"Hillside","distance":4.1,"breakfast":false,"pool":true,"cancel":true}},
                  {"id":"atelier","label":"The Atelier Rooms","subtitle":"City Center · Design hotel","summary":"Small, stylish rooms above a gallery, in the middle of everything.","pros":["Central","Stylish rooms"],"cons":["Small rooms","No breakfast"],"url":"https://example.com/stays/atelier","image":"https://thumb.wikimedia.org/wikipedia/commons/thumb/b/bf/Hotel_Room_Interior_2026-10-13_2.jpg/500px-Hotel_Room_Interior_2026-10-13_2.jpg","values":{"price":214,"rating":8.9,"area":"City Center","distance":0.3,"breakfast":false,"pool":false,"cancel":true}}]}
                """),
            ("Explorer TVs", "explorer", """
                {"id":"tv_options","title":"65-inch TVs under $2,000","description":"Synthetic sample models for UI testing, not live prices or reviews.","attributes":[{"id":"price","label":"Price","type":"money","unit":"USD","better":"lower"},{"id":"rating","label":"Rating","type":"rating","better":"higher"},{"id":"panel","label":"Panel","type":"text"},{"id":"refresh","label":"Refresh rate","type":"number","unit":"Hz","better":"higher"},{"id":"hdmi","label":"HDMI 2.1 ports","type":"number","better":"higher"},{"id":"dolby_vision","label":"Dolby Vision","type":"boolean"},{"id":"year","label":"Model year","type":"number"}],"items":[
                  {"id":"lumen","label":"Lumen OLED 65","subtitle":"OLED · 2025","badge":"Best overall","summary":"Perfect blacks and four full-speed HDMI ports for consoles.","pros":["Perfect blacks","Four HDMI 2.1 ports"],"cons":["Glossy screen"],"values":{"price":1599.99,"rating":4.7,"panel":"OLED","refresh":144,"hdmi":4,"dolby_vision":true,"year":2025}},
                  {"id":"vivid","label":"Vivid QD 65","subtitle":"QD-OLED · 2025","summary":"The brightest colors here, with a matte screen for sunny rooms.","pros":["Very bright","Anti-glare"],"cons":["No Dolby Vision"],"values":{"price":1799,"rating":4.8,"panel":"QD-OLED","refresh":144,"hdmi":4,"dolby_vision":false,"year":2025}},
                  {"id":"nova","label":"Nova Mini-LED 65","subtitle":"Mini-LED · 2025","badge":"Best value","summary":"Punchy HDR for the price, best in bright living rooms.","pros":["Great HDR","Low price"],"cons":["Some blooming"],"values":{"price":999,"rating":4.4,"panel":"Mini-LED","refresh":144,"hdmi":2,"dolby_vision":true,"year":2025}},
                  {"id":"prism","label":"Prism OLED 65 Pro","subtitle":"OLED · 2024","summary":"Last year's flagship, now much cheaper.","pros":["Excellent processing"],"cons":["120 Hz only"],"values":{"price":1449,"rating":4.6,"panel":"OLED","refresh":120,"hdmi":2,"dolby_vision":true,"year":2024}},
                  {"id":"halo","label":"Halo LED 65","subtitle":"LED · 2024","summary":"A simple, affordable set for casual viewing.","pros":["Cheapest here"],"cons":["Dim HDR","60 Hz"],"values":{"price":649,"rating":4.1,"panel":"LED","refresh":60,"hdmi":0,"dolby_vision":false,"year":2024}},
                  {"id":"studio","label":"Studio QD-OLED 65","subtitle":"QD-OLED · 2024","summary":"Accurate out of the box, a favorite for movies.","pros":["Accurate colors"],"cons":["Only two HDMI 2.1 ports"],"values":{"price":1699,"rating":4.7,"panel":"QD-OLED","refresh":120,"hdmi":2,"dolby_vision":true,"year":2024}},
                  {"id":"clarity","label":"Clarity Mini-LED 65","subtitle":"Mini-LED · 2025","summary":"Very bright with strong gaming features.","pros":["165 Hz gaming"],"cons":["Mediocre speakers"],"values":{"price":1199,"rating":4.5,"panel":"Mini-LED","refresh":165,"hdmi":4,"dolby_vision":true,"year":2025}},
                  {"id":"echo","label":"Echo OLED 65 Lite","subtitle":"OLED · 2025","summary":"Entry OLED with great contrast and fewer extras.","pros":["OLED contrast for less"],"cons":["Dimmer than pricier OLEDs"],"values":{"price":1299,"rating":4.5,"panel":"OLED","refresh":120,"hdmi":2,"dolby_vision":true,"year":2025}}]}
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
