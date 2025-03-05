using GaelJ.BlazorCodeMirror6.Models;
using Microsoft.AspNetCore.Components;

namespace SQLFlowUi.Service
{
    // Include any additional namespaces required by your DialogService

    

    public class CompletionService : ComponentBase
    {
        public static List<CodeMirrorCompletion> Completions;

        // Constructor that takes DialogService as a dependency
        public CompletionService()
        {
            
        }

        // Override the asynchronous initialization method
        protected override async Task OnInitializedAsync()
        {
            Completions = await GetMentionCompletions();
        }

        public static async Task<List<CodeMirrorCompletion>> GetMentionCompletions()
        {
            await Task.Delay(1000);
            return await Task.FromResult<List<CodeMirrorCompletion>>(
                [
                    new CodeMirrorCompletion {
                    Label = "dbo.[APCDalene]",
                    Detail = "dbo.[APCDalene]",
                    Info = "APCDalene",
                    Type = "table"
                },
                new CodeMirrorCompletion {
                    Label = "bcd",
                    Detail = "Bob",
                    Info = "Bob is a person",
                    Type = "table"
                },
                new CodeMirrorCompletion {
                    Label = "cde",
                    Detail = "Carol",
                    Info = "Carol is a person",
                    Type = "table"
                },
                new CodeMirrorCompletion {
                    Label = "def",
                    Detail = "Dave",
                    Info = "Dave is a person",
                    Type = "table"
                },
                new CodeMirrorCompletion {
                    Label = "eee",
                    Detail = "Eve",
                    Info = "Eve is a person",
                    Type = "table"
                },
                new CodeMirrorCompletion {
                    Label = "fff",
                    Detail = "Frank",
                    Info = "Frank is a person",
                    Type = "table"
                },
                new CodeMirrorCompletion {
                    Label = "ggg",
                    Detail = "Grace",
                    Info = "Grace is a person",
                    Type = "table"
                },
                new CodeMirrorCompletion {
                    Label = "hhh",
                    Detail = "Heidi",
                    Info = "Heidi is a person",
                    Type = "table"
                },
                new CodeMirrorCompletion {
                    Label = "Ivan",
                    Detail = "Ivan",
                    Info = "Ivan is a person",
                    Type = "table"
                },
                new CodeMirrorCompletion {
                    Label = "Judy",
                    Detail = "Judy",
                    Info = "Judy is a person",
                    Type = "table"
                },
                new CodeMirrorCompletion {
                    Label = "Mallory",
                    Detail = "Mallory",
                    Info = "Mallory is a person",
                    Type = "table"
                },
                new CodeMirrorCompletion {
                    Label = "Oscar",
                    Detail = "Oscar",
                    Info = "Oscar is a person",
                    Type = "table"
                },
                new CodeMirrorCompletion {
                    Label = "Peggy",
                    Detail = "Peggy",
                    Info = "Peggy is a person",
                    Type = "table"
                },
            ]
            );
        }



    }

}

