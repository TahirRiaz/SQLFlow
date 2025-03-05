using Microsoft.JSInterop;
using System;
using System.Threading.Tasks;

namespace SQLFlowUi.Services
{
    public class SQLFlowGraphService
    {
        public readonly IJSRuntime _jsRuntime;

        public SQLFlowGraphService(IJSRuntime jsRuntime)
        {
            _jsRuntime = jsRuntime;
        }

        public async Task<bool> InitializeGraph(string container)
        {
            try
            {
                return await _jsRuntime.InvokeAsync<bool>("initializeGraph", container);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error initializing graph: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> RenderGraphFromJson(string jsonData, string container)
        {
            try
            {
                return await _jsRuntime.InvokeAsync<bool>("renderGraphFromJson", jsonData, container);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error rendering graph: {ex.Message}");
                return false;
            }
        }

        public async Task ShowGraphDialog(string jsonData)
        {
            try
            {
                await _jsRuntime.InvokeVoidAsync("showGraphDialog", jsonData);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error showing graph dialog: {ex.Message}");
            }
        }

        public async Task ZoomIn()
        {
            try
            {
                await _jsRuntime.InvokeVoidAsync("zoomIn");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error zooming in: {ex.Message}");
            }
        }

        public async Task ZoomOut()
        {
            try
            {
                await _jsRuntime.InvokeVoidAsync("zoomOut");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error zooming out: {ex.Message}");
            }
        }

        public async Task ResetZoom()
        {
            try
            {
                await _jsRuntime.InvokeVoidAsync("resetZoom");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error resetting zoom: {ex.Message}");
            }
        }

        public async Task DownloadGraphAsPng(string filename)
        {
            try
            {
                await _jsRuntime.InvokeVoidAsync("downloadGraphAsPng", filename);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error downloading graph: {ex.Message}");
            }
        }

        public async Task ClearHighlights()
        {
            try
            {
                await _jsRuntime.InvokeVoidAsync("SQLFlowGraph.clearHighlights");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error clearing highlights: {ex.Message}");
            }
        }

        public async Task<bool> IsGraphReady()
        {
            try
            {
                return await _jsRuntime.InvokeAsync<bool>("eval", "window.SQLFlowGraph !== undefined");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error checking graph readiness: {ex.Message}");
                return false;
            }
        }

        public async Task<string> GetSelectedNodeId()
        {
            try
            {
                return await _jsRuntime.InvokeAsync<string>("eval", "window.SQLFlowGraph.selectedNodeId || ''");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting selected node ID: {ex.Message}");
                return string.Empty;
            }
        }

        // Enhanced version of HighlightNode that uses the new JavaScript bridge function
        public async Task<bool> HighlightNode(string nodeId)
        {
            try
            {
                // First try the new bridge function
                var result = await _jsRuntime.InvokeAsync<bool>("eval",
                    $"typeof highlightNodeFromBlazor === 'function' ? highlightNodeFromBlazor('{nodeId}') : false");

                if (!result)
                {
                    // Fallback to direct method call
                    await _jsRuntime.InvokeVoidAsync("eval",
                        $"if (window.SQLFlowGraph && typeof window.SQLFlowGraph.highlightNodeById === 'function') {{ window.SQLFlowGraph.highlightNodeById('{nodeId}'); }}");
                    return true;
                }

                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error highlighting node: {ex.Message}");
                return false;
            }
        }

        public async Task HighlightNodeWithAncestors(string nodeId)
        {
            try
            {
                await _jsRuntime.InvokeVoidAsync("SQLFlowGraph.highlightNodeWithAncestorsById", nodeId);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error highlighting node ancestors: {ex.Message}");
            }
        }

        public async Task ShowCodeDialog(string nodeId, string scope = "current")
        {
            try
            {
                await _jsRuntime.InvokeVoidAsync("SQLFlowGraph.showCodeDialogById", nodeId, scope);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error showing code dialog: {ex.Message}");
            }
        }

        // Method to get node details from JavaScript
        public async Task<string> GetNodeDetails(string nodeId)
        {
            try
            {
                return await _jsRuntime.InvokeAsync<string>("eval",
                    $"JSON.stringify(window.SQLFlowGraph._graphData.nodes.find(n => n.id === '{nodeId}'))");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting node details: {ex.Message}");
                return "{}";
            }
        }

        // Debug method to log graph data to console
        public async Task LogGraphData()
        {
            try
            {
                await _jsRuntime.InvokeVoidAsync("eval",
                    "console.log('Graph Data:', window.SQLFlowGraph._graphData)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error logging graph data: {ex.Message}");
            }
        }
    }
}