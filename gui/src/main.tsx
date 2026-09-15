import "@fontsource-variable/inter";
import "@fontsource-variable/jetbrains-mono";
import "./index.css";
import { renderApp } from "./bootstrap";

// SQLFlow's own build registers no GUI modules, so the product renders exactly as it ships. A module build has an
// entry of its own that imports its stylesheet and calls renderApp with its modules.
renderApp();
