namespace PSMultiServer.Addons.CRYPTOSPORIDIUM.BAR
{
	public class BARException : Exception
	{
		public string Filename
		{
			get
			{
				return m_filename;
			}
		}

		public BARException(string message, string filename) : base(message)
		{
			m_filename = filename;
		}

		private string m_filename;
	}
}
