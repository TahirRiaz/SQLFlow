namespace SQLFlowCore.Args
{
    /// <summary>
    /// Represents the schema for event arguments in the SQLFlowCore Engine.
    /// </summary>
    public class EventArgsSchema
    {
        private string _processLog;
        private string _generateCreateCmd;
        private string _createCmd;
        private string _createObject;
        private string _generateRenameCmd;
        private string _renameCmd;
        private string _renameObject;
        private string _metaSrcSchemaCmd;
        private string _metaSrcObject;
        private string _metaTrgSchemaCmd;
        private string _metaTrgObject;

        /// <summary>
        /// Gets or sets the process log.
        /// </summary>
        public string ProcessLog
        {
            get => _processLog;
            set => _processLog = value;
        }

        /// <summary>
        /// Gets or sets the command to generate create.
        /// </summary>
        public string GenerateCreateCmd
        {
            get => _generateCreateCmd;
            set => _generateCreateCmd = value;
        }

        /// <summary>
        /// Gets or sets the create command.
        /// </summary>
        public string CreateCmd
        {
            get => _createCmd;
            set => _createCmd = value;
        }

        /// <summary>
        /// Gets or sets the create object.
        /// </summary>
        public string CreateObject
        {
            get => _createObject;
            set => _createObject = value;
        }

        /// <summary>
        /// Gets or sets the command to generate rename.
        /// </summary>
        public string GenerateRenameCmd
        {
            get => _generateRenameCmd;
            set => _generateRenameCmd = value;
        }

        /// <summary>
        /// Gets or sets the rename command.
        /// </summary>
        public string RenameCmd
        {
            get => _renameCmd;
            set => _renameCmd = value;
        }

        /// <summary>
        /// Gets or sets the rename object.
        /// </summary>
        public string RenameObject
        {
            get => _renameObject;
            set => _renameObject = value;
        }

        /// <summary>
        /// Gets or sets the command for the source schema metadata.
        /// </summary>
        public string MetaSrcSchemaCmd
        {
            get => _metaSrcSchemaCmd;
            set => _metaSrcSchemaCmd = value;
        }

        /// <summary>
        /// Gets or sets the source object metadata.
        /// </summary>
        public string MetaSrcObject
        {
            get => _metaSrcObject;
            set => _metaSrcObject = value;
        }

        /// <summary>
        /// Gets or sets the command for the target schema metadata.
        /// </summary>
        public string MetaTrgSchemaCmd
        {
            get => _metaTrgSchemaCmd;
            set => _metaTrgSchemaCmd = value;
        }

        /// <summary>
        /// Gets or sets the target object metadata.
        /// </summary>
        public string MetaTrgObject
        {
            get => _metaTrgObject;
            set => _metaTrgObject = value;
        }
    }
}
